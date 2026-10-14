namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 一次预测的结果。
/// </summary>
public sealed class PredictionResult
{
    /// <summary>关联的对局标识。</summary>
    public string BattleId { get; set; } = string.Empty;

    /// <summary>预测时间，UTC。</summary>
    public DateTimeOffset PredictedAt { get; set; }

    /// <summary>友方获胜概率，0 到 1。</summary>
    public double AllyWinProbability { get; set; }

    /// <summary>模型置信度，0 到 1。特征缺失越多置信度越低。</summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>模型版本标识。</summary>
    public string ModelVersion { get; set; } = "baseline";

    /// <summary>模型文件哈希，确保预测可追溯。</summary>
    public string ModelHash { get; set; } = string.Empty;

    /// <summary>预测时使用的特征快照，序列化为 JSON 存储。</summary>
    public string FeatureSnapshotJson { get; set; } = "{}";

    /// <summary>特征完整度，true 表示所有关键特征均可用。</summary>
    public bool FeaturesAvailable { get; set; } = true;

    /// <summary>敌方获胜概率。</summary>
    public double EnemyWinProbability => 1.0 - AllyWinProbability;
}
