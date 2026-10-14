using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Prediction;

/// <summary>
/// 特征向量构造。
/// </summary>
/// <remarks>
/// 这是 C# 与 Python 两端共享的特征口径的唯一权威定义。
/// 特征顺序必须与 Python 端 MatchFeatures.feature_names() 完全一致，
/// 任何变动都要同步更新两端并重新导出模型，否则推理输入会错位。
///
/// 顺序契约由 unit test 强制校验。
/// </remarks>
public static class FeatureVectorBuilder
{
    /// <summary>
    /// 特征名有序列表。顺序即模型的输入顺序。
    /// </summary>
    /// <remarks>
    /// 后 12 维为分舰种熟练度特征，v2 追加，旧 20 维顺序不变。
    /// 口径见 src/ml/docs/features-v2-design.md。
    /// </remarks>
    public static readonly string[] FeatureNames =
    [
        "winrate_diff",
        "weighted_winrate_diff",
        "recent_winrate_diff",
        "battles_diff",
        "unicum_diff",
        "ape_diff",
        "tier_diff",
        "player_count_diff",
        "ally_winrate",
        "enemy_winrate",
        "ally_weighted",
        "enemy_weighted",
        "ally_battles",
        "enemy_battles",
        "ally_top_winrate",
        "enemy_top_winrate",
        "ally_ape_count",
        "enemy_ape_count",
        "ally_unicum_count",
        "enemy_unicum_count",
        "ally_class_wr",
        "enemy_class_wr",
        "class_wr_diff",
        "ally_dd_wr",
        "enemy_dd_wr",
        "dd_wr_diff",
        "ally_cv_wr",
        "enemy_cv_wr",
        "cv_wr_diff",
        "dd_count_diff",
        "cv_count_diff",
        "fresh_ship_diff",
    ];

    /// <summary>
    /// 特征数量。
    /// </summary>
    public static int FeatureCount => FeatureNames.Length;

    /// <summary>
    /// 从战斗记录构造特征向量。
    /// </summary>
    /// <param name="battle">战斗记录。</param>
    /// <param name="options">队伍聚合阈值。</param>
    /// <returns>特征值与特征名的配对，顺序固定。</returns>
    public static FeatureVector FromBattle(
        BattleRecord battle,
        TeamAggregationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(battle);

        var ally = TeamAggregator.Aggregate(battle.Allies, options);
        var enemy = TeamAggregator.Aggregate(battle.Enemies, options);
        return FromTeams(ally, enemy);
    }

    /// <summary>
    /// 从双方队伍特征构造向量。
    /// </summary>
    public static FeatureVector FromTeams(TeamFeatures ally, TeamFeatures enemy)
    {
        ArgumentNullException.ThrowIfNull(ally);
        ArgumentNullException.ThrowIfNull(enemy);

        double[] raw =
        [
            ally.AvgAccountWinrate - enemy.AvgAccountWinrate,
            ally.AvgWeightedWinrate - enemy.AvgWeightedWinrate,
            ally.AvgRecentWinrate - enemy.AvgRecentWinrate,
            ally.AvgBattles - enemy.AvgBattles,
            ally.UnicumCount - enemy.UnicumCount,
            ally.ApeCount - enemy.ApeCount,
            ally.AvgTier - enemy.AvgTier,
            ally.PlayerCount - enemy.PlayerCount,
            ally.AvgAccountWinrate,
            enemy.AvgAccountWinrate,
            ally.AvgWeightedWinrate,
            enemy.AvgWeightedWinrate,
            ally.AvgBattles,
            enemy.AvgBattles,
            ally.TopWinrate,
            enemy.TopWinrate,
            ally.ApeCount,
            enemy.ApeCount,
            ally.UnicumCount,
            enemy.UnicumCount,
            ally.AvgClassWr,
            enemy.AvgClassWr,
            ally.AvgClassWr - enemy.AvgClassWr,
            ally.DdWr,
            enemy.DdWr,
            ally.DdWr - enemy.DdWr,
            ally.CvWr,
            enemy.CvWr,
            ally.CvWr - enemy.CvWr,
            ally.DdCount - enemy.DdCount,
            ally.CvCount - enemy.CvCount,
            ally.FreshShipCount - enemy.FreshShipCount,
        ];

        // ONNX 使用 float32，这里显式转换。数量与顺序由 FeatureNames 保证。
        return new FeatureVector
        {
            Values = Array.ConvertAll(raw, v => (float)v),
        };
    }
}

/// <summary>
/// 特征向量。
/// </summary>
public sealed class FeatureVector
{
    /// <summary>特征值，顺序与 <see cref="FeatureVectorBuilder.FeatureNames"/> 对应。</summary>
    public float[] Values { get; set; } = [];

    /// <summary>
    /// 转成 ONNX 推理所需的二维输入张量，形状为 [1, N]。
    /// </summary>
    public float[,] ToTensor()
    {
        var tensor = new float[1, Values.Length];
        for (int i = 0; i < Values.Length; i++)
        {
            tensor[0, i] = Values[i];
        }

        return tensor;
    }
}
