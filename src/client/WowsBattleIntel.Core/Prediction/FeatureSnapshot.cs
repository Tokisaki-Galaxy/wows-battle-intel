using System.Text.Json;
using System.Text.Json.Serialization;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Core.Prediction;

/// <summary>
/// 预测时的特征快照。
/// </summary>
/// <remarks>
/// 把特征序列化落盘是刻意的冗余。它让预测在事后完全可复现，
/// 也让你能在模型迭代后用历史特征重放评估，不必重跑采集链路。
/// 这是校准可信度的基础。
/// </remarks>
public static class FeatureSnapshot
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// 把双方特征与差分序列化成 JSON。
    /// </summary>
    public static string Serialize(
        TeamFeatures ally,
        TeamFeatures enemy,
        TeamFeatureDiff diff)
    {
        ArgumentNullException.ThrowIfNull(ally);
        ArgumentNullException.ThrowIfNull(enemy);
        ArgumentNullException.ThrowIfNull(diff);

        var snapshot = new FeatureSnapshotData
        {
            Ally = FeatureVector.FromTeamFeatures(ally),
            Enemy = FeatureVector.FromTeamFeatures(enemy),
            Diff = diff,
        };

        return JsonSerializer.Serialize(snapshot, SerializerOptions);
    }

    /// <summary>
    /// 从 JSON 反序列化特征快照。用于历史回放评估。
    /// </summary>
    public static FeatureSnapshotData? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<FeatureSnapshotData>(json, SerializerOptions);
    }
}

/// <summary>
/// 特征快照的完整结构。
/// </summary>
public sealed class FeatureSnapshotData
{
    /// <summary>友方特征向量。</summary>
    public FeatureVector Ally { get; set; } = new();

    /// <summary>敌方特征向量。</summary>
    public FeatureVector Enemy { get; set; } = new();

    /// <summary>双方差分。</summary>
    public TeamFeatureDiff Diff { get; set; } = new();
}

/// <summary>
/// 单队的特征向量，序列化友好的扁平结构。
/// </summary>
public sealed class FeatureVector
{
    /// <summary>非机器人玩家数。</summary>
    public int PlayerCount { get; set; }

    /// <summary>机器人数。</summary>
    public int BotCount { get; set; }

    /// <summary>隐藏战绩人数。</summary>
    public int HiddenCount { get; set; }

    /// <summary>平均账号胜率。</summary>
    public double AvgAccountWinrate { get; set; }

    /// <summary>平均加权胜率。</summary>
    public double AvgWeightedWinrate { get; set; }

    /// <summary>平均近期胜率。</summary>
    public double AvgRecentWinrate { get; set; }

    /// <summary>平均场次。</summary>
    public double AvgBattles { get; set; }

    /// <summary>最高胜率。</summary>
    public double TopWinrate { get; set; }

    /// <summary>最低胜率。</summary>
    public double BottomWinrate { get; set; }

    /// <summary>海王数。</summary>
    public int UnicumCount { get; set; }

    /// <summary>猴子数。</summary>
    public int ApeCount { get; set; }

    /// <summary>平均船等级。</summary>
    public double AvgTier { get; set; }

    /// <summary>双排组数。</summary>
    public int Div2Groups { get; set; }

    /// <summary>三排组数。</summary>
    public int Div3Groups { get; set; }

    /// <summary>从队伍特征构造向量。</summary>
    public static FeatureVector FromTeamFeatures(TeamFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);

        return new FeatureVector
        {
            PlayerCount = features.PlayerCount,
            BotCount = features.BotCount,
            HiddenCount = features.HiddenCount,
            AvgAccountWinrate = features.AvgAccountWinrate,
            AvgWeightedWinrate = features.AvgWeightedWinrate,
            AvgRecentWinrate = features.AvgRecentWinrate,
            AvgBattles = features.AvgBattles,
            TopWinrate = features.TopWinrate,
            BottomWinrate = features.BottomWinrate,
            UnicumCount = features.UnicumCount,
            ApeCount = features.ApeCount,
            AvgTier = features.AvgTier,
            Div2Groups = features.Div2Groups,
            Div3Groups = features.Div3Groups,
        };
    }
}
