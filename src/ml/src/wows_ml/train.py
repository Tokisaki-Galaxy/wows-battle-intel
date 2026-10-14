"""模型训练与评估。

迭代顺序按路线图：逻辑回归基线 -> LightGBM -> 集合模型。
任何模型都必须显著优于两个基线：
1. 无信息基线（恒定 0.5，Brier 0.25）
2. 加权胜率差的逻辑函数（C# 端 BaselinePredictor）
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass

import numpy as np
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import roc_auc_score
from sklearn.model_selection import KFold, StratifiedGroupKFold
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import StandardScaler

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


# 分组折的组数下限。组数低于 folds 两倍时 StratifiedGroupKFold 无法
# 保证每折都有训练与测试样本，退回普通 KFold。
MIN_GROUPS_FOR_GROUPED_CV = 20


def group_keys(matches: list[MatchFeatures]) -> np.ndarray:
    """按局参战玩家集合生成交叉验证分组键。

    分组键设计：把本局全部参战者的 account_id 去重排序后用竖线拼接，
    经 SHA256 压成定长字符串。同一玩家出现在两局时，两局的玩家集合
    大概率不同，哈希键也随之不同。

    注意该方案只保证账号层面的隔离近似：键相等意味着两局参战者完全
    相同，键不同但共享部分玩家的两局仍会分属不同组。要把共玩对局
    归入同组需按账号做连通分量合并，见 group_keys_connected。

    普通交叉验证用随机切分，同一老玩家的高胜率样本会同时出现在
    训练与测试集，AUC 虚高。分组折按玩家集合隔离，消除这条泄漏路径。
    """
    keys = []
    for m in matches:
        ids = sorted(set(m.participant_account_ids))
        digest = hashlib.sha256("|".join(ids).encode("utf-8")).hexdigest()
        keys.append(digest)
    return np.asarray(keys, dtype=object)


def group_keys_connected(matches: list[MatchFeatures]) -> np.ndarray:
    """按账号连通分量生成分组键。

    两局共享任一账号即归入同组，组内递归合并，保证同一账号绝不跨
    训练与测试集。实现用并查集：账号为节点，对局内参战者两两并起，
    对局键取其参战者根节点的最小值。

    分组键设计：最终键是组内最小账号标识拼接的哈希，稳定可复现。

    已知局限：若库内存在一个横跨大量对局的高频玩家（例如录制者本人），
    连通分量会塌缩成单组，分组折退化为整体切分。此时应改用
    group_keys 的按局近似键，见 make_cv_splits 的降级逻辑。
    """
    parent: dict[str, str] = {}

    def find(x: str) -> str:
        root = x
        while parent[root] != root:
            root = parent[root]
        while parent[x] != root:
            parent[x], x = root, parent[x]
        return root

    def union(a: str, b: str) -> None:
        parent.setdefault(a, a)
        parent.setdefault(b, b)
        ra, rb = find(a), find(b)
        if ra == rb:
            return
        # 恒把较大根挂到较小根，合并结果与遇到顺序无关，键可复现。
        if ra < rb:
            parent[rb] = ra
        else:
            parent[ra] = rb

    for m in matches:
        ids = sorted(set(m.participant_account_ids))
        for account_id in ids:
            parent.setdefault(account_id, account_id)
        for other in ids[1:]:
            union(ids[0], other)

    # 对局键取组内最小账号的哈希，保证同一分量的对局键一致。
    keys = []
    for m in matches:
        roots = sorted({find(a) for a in set(m.participant_account_ids)})
        digest = hashlib.sha256("|".join(roots).encode("utf-8")).hexdigest()
        keys.append(digest)
    return np.asarray(keys, dtype=object)


def make_cv_splits(
    x: np.ndarray,
    y: np.ndarray,
    groups: np.ndarray,
    folds: int,
    random_state: int = 42,
) -> list[tuple[np.ndarray, np.ndarray]]:
    """构造交叉验证切分，按组数可用性自动降级。

    传入按账号连通分量生成的键时，理论上保证同一账号不跨训练与
    测试集。但连通分量可能塌缩成极少数组（例如录制者出现在所有局），
    分组折失去意义，此时退回普通随机折并保持原调用方式兼容。
    组数充足时用 StratifiedGroupKFold，在隔离玩家之外保持各折
    胜负标签比例接近。
    """
    n_groups = len(set(groups))
    if n_groups >= max(folds * 2, MIN_GROUPS_FOR_GROUPED_CV):
        splitter = StratifiedGroupKFold(
            n_splits=folds, shuffle=True, random_state=random_state
        )
        return list(splitter.split(x, y, groups))

    kf = KFold(n_splits=folds, shuffle=True, random_state=random_state)
    return list(kf.split(x))


def train_logistic(
    matches: list[MatchFeatures],
    folds: int = 5,
    random_state: int = 42,
) -> tuple[Pipeline, TrainResult]:
    """训练逻辑回归基线。

    这是必要的基线：简单、可解释、无需调参。
    复杂模型必须显著超越它才有存在价值。
    """
    x, y = build_matrix(matches)
    valid = ~np.isnan(y)
    x, y = x[valid], y[valid]

    if len(y) < folds:
        raise ValueError(f"样本不足，至少需要 {folds} 条，实际 {len(y)} 条。")

    # 分组键先按账号连通分量生成，塌缩成单组时退回按局玩家集合的
    # 近似键，保证评估不因组数枯竭而整体失效。
    groups = group_keys_connected(matches)[valid]
    if len(set(groups)) < 2 * folds:
        groups = group_keys(matches)[valid]
    splits = make_cv_splits(x, y, groups, folds, random_state)
    reports = []
    all_importance: dict[str, list[float]] = {}

    for train_idx, test_idx in splits:
        model = _make_logistic()
        model.fit(x[train_idx], y[train_idx])
        probs = model.predict_proba(x[test_idx])[:, 1]
        reports.append(build_report(probs, y[test_idx]))

        for name, coef in zip(
            MatchFeatures.feature_names(), _extract_coefficients(model), strict=True
        ):
            all_importance.setdefault(name, []).append(float(coef))

    # 用全量数据训练最终模型。
    final_model = _make_logistic()
    final_model.fit(x, y)

    importance = {k: float(np.mean(v)) for k, v in all_importance.items()}
    result = TrainResult("logistic-regression", reports, importance)
    return final_model, result


def _make_logistic() -> Pipeline:
    """构造逻辑回归管道。

    必须配合标准化：场次类特征在数千量级，胜率在 0 到 1，
    量级差异会导致 lbfgs 不收敛。标准化是应有处理，不是可选优化。
    """
    return Pipeline(
        [
            ("scaler", StandardScaler()),
            (
                "model",
                LogisticRegression(
                    C=1.0,
                    max_iter=2000,
                    solver="lbfgs",
                    random_state=42,
                ),
            ),
        ]
    )


def _extract_coefficients(model) -> np.ndarray:
    """从管道中取出线性模型的系数，用于特征重要性展示。"""
    if isinstance(model, Pipeline) and "model" in model.named_steps:
        inner = model.named_steps["model"]
        return inner.coef_[0]
    return model.coef_[0]


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

    # 分组键策略与 train_logistic 一致：连通分量优先，塌缩退回近似键。
    groups = group_keys_connected(matches)[valid]
    if len(set(groups)) < 2 * folds:
        groups = group_keys(matches)[valid]
    splits = make_cv_splits(x, y, groups, folds, random_state)
    reports = []
    importance_sum: dict[str, float] = {}

    for train_idx, test_idx in splits:
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
