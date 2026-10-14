using WowsBattleIntel.Core.Calibration;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 校准曲线中的一根柱。
/// </summary>
/// <remarks>
/// 柱高代表实际胜率，柱宽代表样本量。样本量少的桶结论不可靠，
/// 因此把样本数一并可视化，避免用一两个样本的桶误导判断。
/// </remarks>
public sealed class CalibrationBucketViewModel
{
    /// <summary>概率区间显示文本，如 0.3-0.4。</summary>
    public required string RangeText { get; init; }

    /// <summary>桶内样本数。</summary>
    public required int SampleCount { get; init; }

    /// <summary>样本数显示文本。</summary>
    public required string SampleCountText { get; init; }

    /// <summary>桶内实际胜率。</summary>
    public required double ActualWinrate { get; init; }

    /// <summary>实际胜率显示文本。</summary>
    public required string ActualWinrateText { get; init; }

    /// <summary>预测均值显示文本。</summary>
    public required string PredictedMeanText { get; init; }

    /// <summary>校准偏差，正值表示实际胜率高于预测，模型偏保守。</summary>
    public required double CalibrationError { get; init; }

    /// <summary>校准偏差显示文本，带正负号。</summary>
    public required string CalibrationErrorText { get; init; }

    /// <summary>柱状条宽度像素，正比于实际胜率。</summary>
    public required double BarWidth { get; init; }

    /// <summary>柱状条颜色，偏差大时标红以示警告。</summary>
    public required string BarBrushKey { get; init; }

    /// <summary>
    /// 从一个校准桶构造展示模型。
    /// </summary>
    /// <param name="bucket">领域层的校准桶。</param>
    /// <param name="maxBarWidth">满宽对应的像素值。</param>
    public static CalibrationBucketViewModel FromBucket(CalibrationBucket bucket, double maxBarWidth)
    {
        ArgumentNullException.ThrowIfNull(bucket);

        // 偏差超过 0.1 视为明显失准，用警示色。
        bool largeDeviation = Math.Abs(bucket.CalibrationError) > 0.1;

        return new CalibrationBucketViewModel
        {
            RangeText = $"{bucket.LowerBound:F1}-{bucket.UpperBound:F1}",
            SampleCount = bucket.SampleCount,
            SampleCountText = bucket.SampleCount.ToString("N0"),
            ActualWinrate = bucket.ActualWinrate,
            ActualWinrateText = bucket.ActualWinrate.ToString("P1"),
            PredictedMeanText = bucket.PredictedMean.ToString("P2"),
            CalibrationError = bucket.CalibrationError,
            CalibrationErrorText = bucket.CalibrationError.ToString("+0.000;-0.000;0.000"),
            BarWidth = Math.Max(0, bucket.ActualWinrate) * maxBarWidth,
            BarBrushKey = largeDeviation ? "NegativeBrush" : "PositiveBrush",
        };
    }
}
