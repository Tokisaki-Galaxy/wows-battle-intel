"""训练到导出的完整管线。

把数据集构造、训练、ONNX 导出、清单写出串成一步，
供命令行调用，也可作为脚本导入。

设计要点：导出后强制做 ONNX 一致性验证，不一致直接报错。
转换引入的数值偏差若不检查，会在客户端表现为难以定位的预测异常。
"""

from __future__ import annotations

import time
from dataclasses import dataclass
from pathlib import Path

from wows_ml.calibration import build_report
from wows_ml.dataset import load_battles
from wows_ml.export import export_to_onnx, verify_onnx, write_model_manifest
from wows_ml.features import MatchFeatures
from wows_ml.train import TrainResult, train_lightgbm, train_logistic


@dataclass
class PipelineResult:
    """管线运行结果。"""

    sample_count: int
    model_path: str
    manifest_path: str
    sha256: str
    feature_count: int
    training: TrainResult
    onnx_verified: bool


def run(
    db_path: str | Path,
    model_path: str | Path,
    manifest_path: str | Path,
    *,
    model_type: str = "logistic",
    allow_future_snapshots: bool = False,
) -> PipelineResult:
    """执行训练到导出的完整管线。

    allow_future_snapshots 默认 False，保证无时间泄漏。
    事后补采数据需显式开启，结果会偏乐观，仅在验证管线时使用。
    """
    matches, stats = load_battles(
        db_path, allow_future_snapshots=allow_future_snapshots
    )

    if len(matches) < 10:
        raise ValueError(
            f"样本不足，至少需要 10 条，实际 {len(matches)} 条。"
            f"统计: {stats}"
        )

    if model_type == "lightgbm":
        model, training = train_lightgbm(matches)
    else:
        model, training = train_logistic(matches)

    export = export_to_onnx(model, model_path, model_name=training.model_name)
    verified = verify_onnx(model_path, model)

    if not verified:
        raise RuntimeError("ONNX 一致性验证失败，导出模型与原始模型输出不一致，已中止。")

    write_model_manifest(
        manifest_path,
        export,
        metrics={
            "brier": training.mean_brier,
            "log_loss": training.mean_log_loss,
            "accuracy": training.mean_accuracy,
        },
        trained_at=int(time.time() * 1000),
    )

    return PipelineResult(
        sample_count=len(matches),
        model_path=export.onnx_path,
        manifest_path=str(manifest_path),
        sha256=export.sha256,
        feature_count=export.feature_count,
        training=training,
        onnx_verified=verified,
    )


def describe(result: PipelineResult) -> str:
    """生成可读的管线结果摘要。"""
    lines = [
        f"样本数:   {result.sample_count}",
        f"模型类型: {result.training.model_name}",
        f"Brier:    {result.training.mean_brier:.4f}",
        f"对数损失: {result.training.mean_log_loss:.4f}",
        f"准确率:   {result.training.mean_accuracy:.2%}",
        f"特征数:   {result.feature_count}",
        f"模型路径: {result.model_path}",
        f"模型哈希: {result.sha256[:16]}...",
        f"ONNX 一致性: {'通过' if result.onnx_verified else '失败'}",
    ]
    return "\n".join(lines)


def baseline_report(matches: list[MatchFeatures]):
    """对样本计算无信息基线，供对照。"""
    import numpy as np

    labels = np.asarray(
        [m.label for m in matches if m.label is not None], dtype=float
    )
    return build_report(np.full(len(labels), 0.5), labels)
