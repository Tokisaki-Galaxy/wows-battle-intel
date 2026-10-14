"""校准指标。

与 C# 端 WowsBattleIntel.Core.Calibration 计算一致，确保两端口径相同。
这是项目的核心产出：量化预测质量，多数同类工具不公布此项。
"""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np


@dataclass
class CalibrationBucket:
    """校准曲线中的一个桶。"""

    lower: float
    upper: float
    sample_count: int
    predicted_mean: float
    actual_winrate: float

    @property
    def error(self) -> float:
        """校准偏差。正值表示模型偏保守，负值表示过于自信。"""
        return self.actual_winrate - self.predicted_mean


@dataclass
class CalibrationReport:
    """完整校准报告。"""

    sample_count: int = 0
    brier_score: float = -1.0
    log_loss: float = -1.0
    accuracy: float = -1.0
    curve: list[CalibrationBucket] = field(default_factory=list)

    def summary(self) -> str:
        """生成可读的摘要。"""
        lines = [
            f"样本数:     {self.sample_count}",
            f"Brier 分数: {self.brier_score:.4f}  (无信息基线 0.25)",
            f"对数损失:   {self.log_loss:.4f}  (无信息基线 0.6931)",
            f"判别准确率: {self.accuracy:.2%}",
        ]
        if self.curve:
            lines.append("")
            lines.append("校准曲线:")
            lines.append(f"  {'区间':<12} {'样本':>6} {'预测均值':>10} {'实际胜率':>10} {'偏差':>10}")
            for b in self.curve:
                lines.append(
                    f"  {b.lower:.1f}-{b.upper:.1f}     {b.sample_count:>6} "
                    f"{b.predicted_mean:>10.3f} {b.actual_winrate:>10.3f} {b.error:>+10.3f}"
                )
        return "\n".join(lines)


def brier_score(predictions: np.ndarray, actuals: np.ndarray) -> float:
    """Brier 分数：预测概率与真实结果的均方误差。越低越好。"""
    if len(predictions) == 0:
        return -1.0
    return float(np.mean((predictions - actuals) ** 2))


def log_loss(predictions: np.ndarray, actuals: np.ndarray) -> float:
    """对数损失。对极端错误更敏感。"""
    if len(predictions) == 0:
        return -1.0
    eps = 1e-15
    p = np.clip(predictions, eps, 1 - eps)
    return float(-np.mean(actuals * np.log(p) + (1 - actuals) * np.log(1 - p)))


def accuracy(predictions: np.ndarray, actuals: np.ndarray) -> float:
    """判别准确率：预测方向与结果一致的比例。"""
    if len(predictions) == 0:
        return -1.0
    predicted_win = predictions >= 0.5
    actual_win = actuals >= 0.5
    return float(np.mean(predicted_win == actual_win))


def calibration_curve(
    predictions: np.ndarray,
    actuals: np.ndarray,
    bucket_count: int = 10,
) -> list[CalibrationBucket]:
    """生成校准曲线。理想情况下实际胜率应接近预测均值。"""
    if len(predictions) == 0:
        return []

    buckets: list[CalibrationBucket] = []
    edges = np.linspace(0, 1, bucket_count + 1)

    for i in range(bucket_count):
        lower, upper = edges[i], edges[i + 1]
        # 最后一个桶包含上界。
        if i == bucket_count - 1:
            mask = (predictions >= lower) & (predictions <= upper)
        else:
            mask = (predictions >= lower) & (predictions < upper)

        count = int(np.sum(mask))
        if count == 0:
            continue

        buckets.append(
            CalibrationBucket(
                lower=float(lower),
                upper=float(upper),
                sample_count=count,
                predicted_mean=float(np.mean(predictions[mask])),
                actual_winrate=float(np.mean(actuals[mask])),
            )
        )

    return buckets


def build_report(predictions: np.ndarray, actuals: np.ndarray) -> CalibrationReport:
    """从预测与真实结果构造完整报告。"""
    predictions = np.asarray(predictions, dtype=float)
    actuals = np.asarray(actuals, dtype=float)

    return CalibrationReport(
        sample_count=len(predictions),
        brier_score=brier_score(predictions, actuals),
        log_loss=log_loss(predictions, actuals),
        accuracy=accuracy(predictions, actuals),
        curve=calibration_curve(predictions, actuals),
    )


def baseline_probabilities(matches) -> np.ndarray:
    """基线预测：用双方加权胜率差过逻辑函数。

    与 C# 端 BaselinePredictor 一致，斜率 4.0。
    作为任何训练模型必须超越的基线。
    """
    probs = []
    for m in matches:
        ally = m.ally
        enemy = m.enemy
        # 优先加权胜率，缺失时退回账号胜率。
        if ally.avg_weighted_winrate > 0 or enemy.avg_weighted_winrate > 0:
            diff = ally.avg_weighted_winrate - enemy.avg_weighted_winrate
        else:
            diff = ally.avg_account_winrate - enemy.avg_account_winrate
        probs.append(1.0 / (1.0 + np.exp(-diff * 4.0)))
    return np.asarray(probs, dtype=float)
