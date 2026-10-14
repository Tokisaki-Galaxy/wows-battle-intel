"""模型训练与评估。

迭代顺序按路线图：逻辑回归基线 -> LightGBM -> 集合模型。
任何模型都必须显著优于两个基线：
1. 无信息基线（恒定 0.5，Brier 0.25）
2. 加权胜率差的逻辑函数（C# 端 BaselinePredictor）
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import roc_auc_score
from sklearn.model_selection import KFold

from wows_ml.calibration import CalibrationReport, build_report
from wows_ml.features import MatchFeatures


@dataclass
class TrainResult:
    """一次训练的结果。"""

    model_name: str
    fold_reports: list[CalibrationReport]
    feature_importance: dict[str, float]

    @property
    def mean_brier(self) -> float:
        if not self.fold_reports:
            return -1.0
        return float(np.mean([r.brier_score for r in self.fold_reports]))

    @property
    def mean_log_loss(self) -> float:
        if not self.fold_reports:
            return -1.0
        return float(np.mean([r.log_loss for r in self.fold_reports]))

    @property
    def mean_accuracy(self) -> float:
        if not self.fold_reports:
            return -1.0
        return float(np.mean([r.accuracy for r in self.fold_reports]))

    def summary(self) -> str:
        lines = [
            f"模型: {self.model_name}",
            f"交叉验证折数: {len(self.fold_reports)}",
            f"平均 Brier:  {self.mean_brier:.4f}",
            f"平均对数损失: {self.mean_log_loss:.4f}",
            f"平均准确率:  {self.mean_accuracy:.2%}",
        ]
        if self.feature_importance:
            lines.append("")
            lines.append("特征重要性（前 10）:")
            ranked = sorted(self.feature_importance.items(), key=lambda x: -abs(x[1]))
            for name, value in ranked[:10]:
                lines.append(f"  {name:<24} {value:>+10.4f}")
        return "\n".join(lines)


def build_matrix(matches: list[MatchFeatures]) -> tuple[np.ndarray, np.ndarray]:
    """把对局特征转成矩阵与标签向量。"""
    names = MatchFeatures.feature_names()
    rows = []
    labels = []
    for m in matches:
        vec = m.to_vector()
        rows.append([vec[n] for n in names])
        labels.append(m.label if m.label is not None else np.nan)
    return np.asarray(rows, dtype=float), np.asarray(labels, dtype=float)


def train_logistic(
    matches: list[MatchFeatures],
    folds: int = 5,
    random_state: int = 42,
) -> tuple[LogisticRegression, TrainResult]:
    """训练逻辑回归基线。

    这是必要的基线：简单、可解释、无需调参。
    复杂模型必须显著超越它才有存在价值。
    """
    x, y = build_matrix(matches)
    valid = ~np.isnan(y)
    x, y = x[valid], y[valid]

    if len(y) < folds:
        raise ValueError(f"样本不足，至少需要 {folds} 条，实际 {len(y)} 条。")

    kf = KFold(n_splits=folds, shuffle=True, random_state=random_state)
    reports = []
    all_importance: dict[str, list[float]] = {}

    for train_idx, test_idx in kf.split(x):
        model = _make_logistic()
        model.fit(x[train_idx], y[train_idx])
        probs = model.predict_proba(x[test_idx])[:, 1]
        reports.append(build_report(probs, y[test_idx]))

        for name, coef in zip(MatchFeatures.feature_names(), model.coef_[0], strict=True):
            all_importance.setdefault(name, []).append(float(coef))

    # 用全量数据训练最终模型。
    final_model = _make_logistic()
    final_model.fit(x, y)

    importance = {k: float(np.mean(v)) for k, v in all_importance.items()}
    result = TrainResult("logistic-regression", reports, importance)
    return final_model, result


def _make_logistic() -> LogisticRegression:
    return LogisticRegression(
        C=1.0,
        max_iter=1000,
        solver="lbfgs",
        random_state=42,
    )


def train_lightgbm(
    matches: list[MatchFeatures],
    folds: int = 5,
    random_state: int = 42,
) -> tuple[object, TrainResult]:
    """训练 LightGBM 模型。

    LightGBM 通常能吃掉梯度提升相对于线性模型的大部分收益。
    在小样本上需要限制树的复杂度以避免过拟合。
    """
    import lightgbm as lgb

    x, y = build_matrix(matches)
    valid = ~np.isnan(y)
    x, y = x[valid], y[valid]

    if len(y) < folds:
        raise ValueError(f"样本不足，至少需要 {folds} 条，实际 {len(y)} 条。")

    kf = KFold(n_splits=folds, shuffle=True, random_state=random_state)
    reports = []
    importance_sum: dict[str, float] = {}

    for train_idx, test_idx in kf.split(x):
        model = _make_lightgbm(lgb, random_state)
        model.fit(x[train_idx], y[train_idx])
        probs = model.predict_proba(x[test_idx])[:, 1]
        reports.append(build_report(probs, y[test_idx]))

        for name, imp in zip(
            MatchFeatures.feature_names(), model.feature_importances_, strict=True
        ):
            importance_sum[name] = importance_sum.get(name, 0.0) + float(imp)

    final_model = _make_lightgbm(lgb, random_state)
    final_model.fit(x, y)

    importance = {k: v / folds for k, v in importance_sum.items()}
    result = TrainResult("lightgbm", reports, importance)
    return final_model, result


def _make_lightgbm(lgb, random_state: int):
    """构造 LightGBM 分类器。

    小样本下调低树深与叶子数，配合较强的正则化。
    """
    return lgb.LGBMClassifier(
        n_estimators=300,
        learning_rate=0.03,
        max_depth=4,
        num_leaves=15,
        min_child_samples=20,
        subsample=0.8,
        colsample_bytree=0.8,
        reg_alpha=0.1,
        reg_lambda=0.1,
        random_state=random_state,
        verbose=-1,
    )


def compare_baselines(matches: list[MatchFeatures]) -> str:
    """对比无信息基线与加权胜率差基线的表现。

    没有有效特征时，两者都退化到 0.25 附近，说明特征缺失。
    """
    from wows_ml.calibration import baseline_probabilities

    _, y = build_matrix(matches)
    valid = ~np.isnan(y)
    y_valid = y[valid]

    uninformative = np.full(len(y_valid), 0.5)
    weighted = baseline_probabilities([m for m, v in zip(matches, valid, strict=True) if v])

    lines = ["基线对比（无信息 vs 加权胜率差逻辑函数）", ""]
    lines.append(f"样本数: {len(y_valid)}")
    lines.append("")
    lines.append("无信息基线（恒定 0.5）:")
    lines.append(build_report(uninformative, y_valid).summary())
    lines.append("")
    lines.append("加权胜率差基线:")
    lines.append(build_report(weighted, y_valid).summary())

    # 若特征全为哨兵值，加权基线会退化，AUC 无意义。
    if len(np.unique(weighted)) > 1:
        auc = roc_auc_score(y_valid, weighted)
        lines.append("")
        lines.append(f"加权基线 AUC: {auc:.4f}")

    return "\n".join(lines)
