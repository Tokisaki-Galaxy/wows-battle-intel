"""模型导出。

把训练好的模型导出为 ONNX，供客户端用 ONNX Runtime 本地推理。
见 ADR 0003：推理在客户端，模型由服务器下发。

导出的 ONNX 必须与 C# 端的特征顺序严格一致。特征顺序由
MatchFeatures.feature_names() 定义，任何变动都要同步更新两端。
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from wows_ml.features import MatchFeatures


@dataclass
class ExportResult:
    """导出结果。"""

    onnx_path: str
    sha256: str
    feature_count: int
    model_name: str


def export_to_onnx(
    model,
    output_path: str | Path,
    *,
    model_name: str = "wows-battle-intel",
    sample_count: int = 5,
) -> ExportResult:
    """把 sklearn 兼容模型导出为 ONNX。

    支持逻辑回归与 LightGBM。导出后需用 onnxruntime 验证输出一致。
    """
    from skl2onnx import to_onnx

    feature_names = MatchFeatures.feature_names()
    feature_count = len(feature_names)

    # 构造与特征数一致的样例输入，用于确定 ONNX 图的输入形状。
    x_sample = np.zeros((sample_count, feature_count), dtype=np.float32)
    # 稍作变化避免退化输入导致图优化异常。
    x_sample[:, 0] = np.linspace(-0.1, 0.1, sample_count)

    onnx_model = to_onnx(
        model,
        x_sample,
        options={"zipmap": False},  # 输出原始概率张量而非字典
        target_opset=15,
    )

    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(onnx_model.SerializeToString())

    digest = hashlib.sha256(output_path.read_bytes()).hexdigest()

    return ExportResult(
        onnx_path=str(output_path),
        sha256=digest,
        feature_count=feature_count,
        model_name=model_name,
    )


def verify_onnx(
    onnx_path: str | Path,
    model,
    *,
    sample_count: int = 50,
) -> bool:
    """验证 ONNX 输出与原始模型一致。

    这是必须的步骤：ONNX 转换可能引入数值差异，
    客户端推理前必须确认转换无损。
    """
    import onnxruntime as ort

    feature_count = len(MatchFeatures.feature_names())
    rng = np.random.default_rng(42)
    x = rng.normal(0, 0.1, size=(sample_count, feature_count)).astype(np.float32)

    # 原始模型预测。
    original = model.predict_proba(x)[:, 1]

    # ONNX 推理。
    session = ort.InferenceSession(
        str(onnx_path),
        providers=["CPUExecutionProvider"],
    )
    input_name = session.get_inputs()[0].name

    # skl2onnx 会输出 label 与 probabilities 两个张量，需按名称取概率。
    outputs = session.run(None, {input_name: x})
    output_names = [o.name for o in session.get_outputs()]
    if "probabilities" in output_names:
        raw = outputs[output_names.index("probabilities")]
    else:
        raw = outputs[-1]

    # 输出可能是二维 [N, 2]，取正类概率。
    onnx_probs = raw[:, 1] if raw.ndim == 2 and raw.shape[1] == 2 else raw.ravel()

    max_diff = float(np.max(np.abs(original - onnx_probs)))
    # 允许浮点转换的微小误差。
    return max_diff < 1e-4


def write_model_manifest(
    output_path: str | Path,
    export_result: ExportResult,
    *,
    metrics: dict[str, float],
    trained_at: int,
) -> None:
    """写出模型清单，供上传到中央库登记。

    清单包含版本、哈希、特征数、指标，与云端 models 表对应。
    """
    import json

    manifest = {
        "modelVersion": export_result.model_name,
        "sha256": export_result.sha256,
        "featureCount": export_result.feature_count,
        "featureNames": MatchFeatures.feature_names(),
        "trainedAt": trained_at,
        "metrics": metrics,
    }

    Path(output_path).write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False),
        encoding="utf-8",
    )
