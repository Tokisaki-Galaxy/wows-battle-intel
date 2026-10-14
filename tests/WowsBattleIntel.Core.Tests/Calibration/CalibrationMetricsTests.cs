using WowsBattleIntel.Core.Calibration;

namespace WowsBattleIntel.Core.Tests.Calibration;

public class CalibrationMetricsTests
{
    [Fact]
    public void BrierScore_PerfectPredictions_ReturnsZero()
    {
        var predictions = new (double, double)[]
        {
            (1.0, 1.0),
            (0.0, 0.0),
            (1.0, 1.0),
        };

        double brier = CalibrationMetrics.BrierScore(predictions);

        Assert.Equal(0.0, brier, precision: 10);
    }

    [Fact]
    public void BrierScore_UninformativeFiftyPercent_ReturnsQuarter()
    {
        // 永远预测 0.5，一半对一半错，Brier 应为 0.25。
        var predictions = new (double, double)[]
        {
            (0.5, 1.0),
            (0.5, 0.0),
            (0.5, 1.0),
            (0.5, 0.0),
        };

        double brier = CalibrationMetrics.BrierScore(predictions);

        Assert.Equal(0.25, brier, precision: 10);
    }

    [Fact]
    public void BrierScore_EmptyInput_ReturnsNegativeOne()
    {
        double brier = CalibrationMetrics.BrierScore([]);
        Assert.Equal(-1, brier);
    }

    [Fact]
    public void Accuracy_AllCorrect_ReturnsOne()
    {
        var predictions = new (double, double)[]
        {
            (0.8, 1.0),  // 预测胜，实际胜
            (0.2, 0.0),  // 预测负，实际负
        };

        double accuracy = CalibrationMetrics.Accuracy(predictions);

        Assert.Equal(1.0, accuracy, precision: 10);
    }

    [Fact]
    public void Accuracy_AllWrong_ReturnsZero()
    {
        var predictions = new (double, double)[]
        {
            (0.8, 0.0),
            (0.2, 1.0),
        };

        double accuracy = CalibrationMetrics.Accuracy(predictions);

        Assert.Equal(0.0, accuracy, precision: 10);
    }

    [Fact]
    public void CalibrationCurve_BucketsByPredictedProbability()
    {
        var predictions = new (double, double)[]
        {
            (0.05, 0.0),
            (0.05, 0.0),
            (0.95, 1.0),
            (0.95, 1.0),
        };

        var curve = CalibrationMetrics.CalibrationCurve(predictions, bucketCount: 10);

        // 只有第 0 桶和第 9 桶非空。
        Assert.Equal(2, curve.Count);
        var first = curve.First(b => b.LowerBound == 0.0);
        var last = curve.First(b => Math.Abs(b.UpperBound - 1.0) < 1e-9);
        Assert.Equal(2, first.SampleCount);
        Assert.Equal(0.0, first.ActualWinrate, precision: 10);
        Assert.Equal(1.0, last.ActualWinrate, precision: 10);
    }

    [Fact]
    public void CalibrationCurve_WellCalibrated_HasSmallError()
    {
        // 预测 0.8 的样本里，实际胜率也接近 0.8。
        var predictions = new List<(double, double)>();
        for (int i = 0; i < 80; i++)
        {
            predictions.Add((0.8, 1.0));
        }
        for (int i = 0; i < 20; i++)
        {
            predictions.Add((0.8, 0.0));
        }

        var curve = CalibrationMetrics.CalibrationCurve(predictions);
        var bucket = Assert.Single(curve);

        Assert.Equal(0.8, bucket.ActualWinrate, precision: 6);
        Assert.Equal(100, bucket.SampleCount);
    }

    [Fact]
    public void LogLoss_PerfectPredictions_ReturnsNearZero()
    {
        var predictions = new (double, double)[]
        {
            (0.999999, 1.0),
            (0.000001, 0.0),
        };

        double loss = CalibrationMetrics.LogLoss(predictions);

        Assert.True(loss < 0.01);
    }

    [Fact]
    public void CalibrationError_Overconfident_IsNegative()
    {
        var bucket = new CalibrationBucket
        {
            PredictedMean = 0.9,
            ActualWinrate = 0.6,
        };

        // 预测 0.9 实际 0.6，说明模型过于自信，误差为负。
        Assert.Equal(-0.3, bucket.CalibrationError, precision: 10);
    }
}
