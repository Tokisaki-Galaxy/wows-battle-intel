using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Calibration;

/// <summary>
/// 预测校准统计。
/// </summary>
/// <remarks>
/// 这是本项目相对现有水表工具的核心差异点。大多数工具只展示预测，
/// 从不公布准确率。这里把预测质量量化，用数据说话。
///
/// 主要指标：
/// - Brier 分数：预测概率与真实结果的均方误差，越低越好。0 为完美，0.25 为无信息基线。
/// - 校准曲线：把预测概率分桶，看每桶的实际胜率是否与预测一致。
/// - 对数损失：对极端错误更敏感的指标。
/// </remarks>
public static class CalibrationMetrics
{
    /// <summary>
    /// 计算 Brier 分数。
    /// </summary>
    /// <param name="predictions">预测概率与实际结果的配对。</param>
    /// <returns>Brier 分数，范围 0 到 1，越低越好。空输入返回 -1。</returns>
    public static double BrierScore(IEnumerable<(double Predicted, double Actual)> predictions)
    {
        ArgumentNullException.ThrowIfNull(predictions);

        double sum = 0;
        int count = 0;
        foreach (var (predicted, actual) in predictions)
        {
            double diff = predicted - actual;
            sum += diff * diff;
            count++;
        }

        return count == 0 ? -1 : sum / count;
    }

    /// <summary>
    /// 计算对数损失。
    /// </summary>
    /// <param name="predictions">预测概率与实际结果的配对。</param>
    /// <returns>对数损失，越低越好。空输入返回 -1。</returns>
    public static double LogLoss(IEnumerable<(double Predicted, double Actual)> predictions)
    {
        ArgumentNullException.ThrowIfNull(predictions);

        const double epsilon = 1e-15;
        double sum = 0;
        int count = 0;
        foreach (var (predicted, actual) in predictions)
        {
            double p = Math.Clamp(predicted, epsilon, 1 - epsilon);
            sum += -(actual * Math.Log(p) + (1 - actual) * Math.Log(1 - p));
            count++;
        }

        return count == 0 ? -1 : sum / count;
    }

    /// <summary>
    /// 计算判别准确率，即预测方向与结果一致的比例。
    /// </summary>
    /// <param name="predictions">预测概率与实际结果的配对。</param>
    /// <returns>准确率，0 到 1。空输入返回 -1。</returns>
    public static double Accuracy(IEnumerable<(double Predicted, double Actual)> predictions)
    {
        ArgumentNullException.ThrowIfNull(predictions);

        int correct = 0;
        int count = 0;
        foreach (var (predicted, actual) in predictions)
        {
            // 预测概率大于 0.5 判为获胜。
            bool predictedWin = predicted >= 0.5;
            bool actualWin = actual >= 0.5;
            if (predictedWin == actualWin)
            {
                correct++;
            }
            count++;
        }

        return count == 0 ? -1 : (double)correct / count;
    }

    /// <summary>
    /// 生成校准曲线。把预测概率分成若干等宽桶，统计每桶的实际胜率。
    /// </summary>
    /// <param name="predictions">预测概率与实际结果的配对。</param>
    /// <param name="bucketCount">分桶数量，默认 10。</param>
    /// <returns>各桶的统计。理想情况下实际胜率应接近预测均值。</returns>
    public static IReadOnlyList<CalibrationBucket> CalibrationCurve(
        IEnumerable<(double Predicted, double Actual)> predictions,
        int bucketCount = 10)
    {
        ArgumentNullException.ThrowIfNull(predictions);
        if (bucketCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketCount), "分桶数量必须为正数。");
        }

        var buckets = new List<double>[bucketCount];
        for (int i = 0; i < bucketCount; i++)
        {
            buckets[i] = [];
        }

        foreach (var (predicted, actual) in predictions)
        {
            int index = (int)(Math.Clamp(predicted, 0, 1 - 1e-9) * bucketCount);
            buckets[index].Add(actual);
        }

        var result = new List<CalibrationBucket>();
        for (int i = 0; i < bucketCount; i++)
        {
            var bucket = buckets[i];
            if (bucket.Count == 0)
            {
                continue;
            }

            double lower = (double)i / bucketCount;
            double upper = (double)(i + 1) / bucketCount;
            result.Add(new CalibrationBucket
            {
                LowerBound = lower,
                UpperBound = upper,
                SampleCount = bucket.Count,
                PredictedMean = (lower + upper) / 2,
                ActualWinrate = bucket.Average(),
            });
        }

        return result;
    }

    /// <summary>
    /// 从预测记录与真实结果计算完整的校准报告。
    /// </summary>
    public static CalibrationReport BuildReport(IEnumerable<(PredictionResult Prediction, BattleOutcome Outcome)> data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var pairs = data
            .Select(x => (x.Prediction.AllyWinProbability, x.Outcome.ToBinaryTarget()))
            .Where(x => x.Item2.HasValue)
            .Select(x => (x.Item1, x.Item2!.Value))
            .ToList();

        return new CalibrationReport
        {
            SampleCount = pairs.Count,
            BrierScore = BrierScore(pairs),
            LogLoss = LogLoss(pairs),
            Accuracy = Accuracy(pairs),
            Curve = CalibrationCurve(pairs),
        };
    }
}

/// <summary>
/// 校准曲线中的一个桶。
/// </summary>
public sealed class CalibrationBucket
{
    /// <summary>桶下界，含。</summary>
    public double LowerBound { get; set; }

    /// <summary>桶上界，不含。</summary>
    public double UpperBound { get; set; }

    /// <summary>桶内样本数。</summary>
    public int SampleCount { get; set; }

    /// <summary>桶内预测概率均值。</summary>
    public double PredictedMean { get; set; }

    /// <summary>桶内实际胜率。</summary>
    public double ActualWinrate { get; set; }

    /// <summary>校准偏差，实际胜率减预测均值。正值表示模型偏保守。</summary>
    public double CalibrationError => ActualWinrate - PredictedMean;
}

/// <summary>
/// 完整的校准报告。
/// </summary>
public sealed class CalibrationReport
{
    /// <summary>参与统计的样本数。</summary>
    public int SampleCount { get; set; }

    /// <summary>Brier 分数。</summary>
    public double BrierScore { get; set; }

    /// <summary>对数损失。</summary>
    public double LogLoss { get; set; }

    /// <summary>判别准确率。</summary>
    public double Accuracy { get; set; }

    /// <summary>校准曲线。</summary>
    public IReadOnlyList<CalibrationBucket> Curve { get; set; } = [];
}
