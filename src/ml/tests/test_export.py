"""模型导出测试。

验证 ONNX 导出与原始模型输出一致。用少量合成样本快速完成。
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from wows_ml.export import export_to_onnx, verify_onnx, write_model_manifest
from wows_ml.features import MatchFeatures


def make_tiny_model():
    """构造一个已拟合的微型逻辑回归模型。"""
    from sklearn.linear_model import LogisticRegression

    feature_count = len(MatchFeatures.feature_names())
    rng = np.random.default_rng(0)
    x = rng.normal(0, 1, size=(40, feature_count))
    # 用第一个特征构造线性可分标签，保证模型能学到东西。
    y = (x[:, 0] > 0).astype(int)

    model = LogisticRegression(max_iter=500)
    model.fit(x, y)
    return model


class TestOnnxExport:
    def test_export_creates_file(self, tmp_path: Path) -> None:
        model = make_tiny_model()
        out = tmp_path / "model.onnx"

        result = export_to_onnx(model, out)

        assert out.exists()
        assert result.onnx_path == str(out)
        assert result.feature_count == len(MatchFeatures.feature_names())
        assert len(result.sha256) == 64

    def test_exported_model_matches_original(self, tmp_path: Path) -> None:
        model = make_tiny_model()
        out = tmp_path / "model.onnx"

        export_to_onnx(model, out)

        assert verify_onnx(out, model)

    def test_manifest_contains_feature_names(self, tmp_path: Path) -> None:
        model = make_tiny_model()
        out = tmp_path / "model.onnx"
        manifest_path = tmp_path / "manifest.json"

        result = export_to_onnx(model, out)
        write_model_manifest(
            manifest_path,
            result,
            metrics={"brier": 0.24},
            trained_at=1_700_000_000,
        )

        import json

        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        assert manifest["featureCount"] == result.feature_count
        assert manifest["sha256"] == result.sha256
        assert manifest["featureNames"] == MatchFeatures.feature_names()
        assert manifest["metrics"]["brier"] == pytest.approx(0.24)

    def test_export_creates_parent_dirs(self, tmp_path: Path) -> None:
        model = make_tiny_model()
        out = tmp_path / "nested" / "dir" / "model.onnx"

        export_to_onnx(model, out)

        assert out.exists()
