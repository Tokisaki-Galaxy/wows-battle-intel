"""校准指标测试。

验证 Brier 分数、对数损失、准确率、校准曲线的数值正确性。
"""

from __future__ import annotations

import numpy as np
import pytest

from wows_ml.calibration import (
    accuracy,
    brier_score,
    build_report,
    calibration_curve,
    log_loss,
)


class TestBrierScore:
    def test_perfect_predictions_zero(self) -> None:
        preds = np.array([1.0, 0.0, 1.0])
        actuals = np.array([1.0, 0.0, 1.0])

        assert brier_score(preds, actuals) == pytest.approx(0.0)

    def test_uninformative_half_quarter(self) -> None:
        # 恒定 0.5，一半对一半错，Brier 为 0.25。
        preds = np.array([0.5, 0.5, 0.5, 0.5])
        actuals = np.array([1.0, 0.0, 1.0, 0.0])

        assert brier_score(preds, actuals) == pytest.approx(0.25)

    def test_empty_returns_negative_one(self) -> None:
        assert brier_score(np.array([]), np.array([])) == -1.0


class TestLogLoss:
    def test_perfect_near_zero(self) -> None:
        preds = np.array([0.999999, 0.000001])
        actuals = np.array([1.0, 0.0])

        assert log_loss(preds, actuals) < 0.01

    def test_uninformative_ln2(self) -> None:
        preds = np.array([0.5, 0.5, 0.5, 0.5])
        actuals = np.array([1.0, 0.0, 1.0, 0.0])

        assert log_loss(preds, actuals) == pytest.approx(np.log(2), abs=1e-6)

    def test_empty_returns_negative_one(self) -> None:
        assert log_loss(np.array([]), np.array([])) == -1.0


class TestAccuracy:
    def test_all_correct(self) -> None:
        preds = np.array([0.8, 0.2])
        actuals = np.array([1.0, 0.0])

        assert accuracy(preds, actuals) == pytest.approx(1.0)

    def test_all_wrong(self) -> None:
        preds = np.array([0.8, 0.2])
        actuals = np.array([0.0, 1.0])

        assert accuracy(preds, actuals) == pytest.approx(0.0)

    def test_empty_returns_negative_one(self) -> None:
        assert accuracy(np.array([]), np.array([])) == -1.0


class TestCalibrationCurve:
    def test_buckets_by_prediction(self) -> None:
        preds = np.array([0.05, 0.05, 0.95, 0.95])
        actuals = np.array([0.0, 0.0, 1.0, 1.0])

        curve = calibration_curve(preds, actuals, bucket_count=10)

        assert len(curve) == 2
        first = curve[0]
        last = curve[-1]
        assert first.sample_count == 2
        assert first.actual_winrate == pytest.approx(0.0)
        assert last.actual_winrate == pytest.approx(1.0)

    def test_well_calibrated_small_error(self) -> None:
        # 预测 0.8 的样本里，实际胜率接近 0.8。
        preds = np.concatenate([np.full(80, 0.8), np.full(20, 0.8)])
        actuals = np.concatenate([np.ones(80), np.zeros(20)])

        curve = calibration_curve(preds, actuals)

        assert len(curve) == 1
        assert curve[0].actual_winrate == pytest.approx(0.8)
        assert curve[0].sample_count == 100

    def test_empty_returns_empty(self) -> None:
        assert calibration_curve(np.array([]), np.array([])) == []

    def test_error_sign(self) -> None:
        # 预测 0.9 实际 0.6，模型过于自信，误差为负。
        preds = np.concatenate([np.full(60, 0.9), np.full(40, 0.9)])
        actuals = np.concatenate([np.ones(60), np.zeros(40)])

        curve = calibration_curve(preds, actuals)

        assert curve[0].error < 0


class TestBuildReport:
    def test_produces_complete_report(self) -> None:
        preds = np.array([0.7, 0.3, 0.6, 0.4])
        actuals = np.array([1.0, 0.0, 1.0, 0.0])

        report = build_report(preds, actuals)

        assert report.sample_count == 4
        assert report.brier_score > 0
        assert report.log_loss > 0
        assert report.accuracy == pytest.approx(1.0)
        assert len(report.curve) > 0

    def test_summary_is_readable(self) -> None:
        preds = np.array([0.7, 0.3])
        actuals = np.array([1.0, 0.0])

        report = build_report(preds, actuals)
        summary = report.summary()

        assert "Brier" in summary
        assert "样本数" in summary
