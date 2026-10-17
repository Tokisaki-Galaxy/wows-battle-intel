using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Scoring;

/// <summary>
/// 把单玩家特征聚合成队伍层面的特征。
/// </summary>
/// <remarks>
/// 阈值沿用 ApeRadar 默认口径：低于 47% 为猴，高于 58% 且场次超 500 为海王。
/// 这些阈值可配置，但默认保持稳定便于对照。
/// </remarks>
public static class TeamAggregator
{
    /// <summary>默认的猴子胜率阈值，低于此值视为低水平玩家。</summary>
    public const double DefaultApeWinrateThreshold = 0.47;

    /// <summary>默认的海王胜率阈值，高于此值视为高水平玩家。</summary>
    public const double DefaultUnicumWinrateThreshold = 0.58;

    /// <summary>默认的海王场次阈值，避免小样本误判。</summary>
    public const double DefaultUnicumBattlesThreshold = 500;

    /// <summary>经验贝叶斯收缩的先验等效场次，与 Python 端 SHRINKAGE_M 一致。</summary>
    public const double ShrinkageM = 40.0;

    /// <summary>单船白板判定阈值，与 Python 端 FRESH_SHIP_BATTLES 一致。</summary>
    public const double FreshShipBattles = 10.0;

    /// <summary>
    /// 经验贝叶斯收缩胜率。shrunk = (wins + m * prior) / (battles + m)。
    /// 先验用账号自身胜率，缺失时取 0.5。口径见 src/ml/docs/features-v2-design.md。
    /// </summary>
    public static double ShrunkWinrate(double battles, double wins, double accountWinrate)
    {
        double prior = accountWinrate >= 0 ? accountWinrate : 0.5;
        return (wins + ShrinkageM * prior) / (battles + ShrinkageM);
    }

    /// <summary>
    /// 按账号胜率与场次判定玩家档位。
    /// </summary>
    /// <remarks>
    /// 名单文案与胜率列着色共用这一处判定，避免界面里再硬编码阈值后与
    /// 聚合口径分叉。场次门槛只对海王生效，避免小样本误判。
    /// </remarks>
    public static PlayerTier ClassifyPlayer(double accountWinrate, double accountBattles)
    {
        if (accountWinrate > DefaultUnicumWinrateThreshold
            && accountBattles >= DefaultUnicumBattlesThreshold)
        {
            return PlayerTier.Unicum;
        }

        return accountWinrate <= DefaultApeWinrateThreshold ? PlayerTier.Ape : PlayerTier.Normal;
    }

    /// <summary>
    /// 聚合一个队伍的特征。
    /// </summary>
    /// <param name="players">队伍成员。</param>
    /// <param name="options">阈值参数。</param>
    public static TeamFeatures Aggregate(
        IEnumerable<PlayerRecord> players,
        TeamAggregationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(players);
        var opt = options ?? TeamAggregationOptions.Default;

        var humanPlayers = players.Where(p => !p.IsBot).ToList();
        var withStats = humanPlayers.Where(p => p.HasStats).ToList();

        var result = new TeamFeatures
        {
            PlayerCount = humanPlayers.Count,
            BotCount = players.Count(p => p.IsBot),
            HiddenCount = humanPlayers.Count(p => p.IsHidden),
        };

        if (withStats.Count == 0)
        {
            return result;
        }

        result.AvgAccountWinrate = withStats.Average(p => p.AccountWinrate);
        result.AvgBattles = withStats.Average(p => p.AccountBattles);
        result.TopWinrate = withStats.Max(p => p.AccountWinrate);
        result.BottomWinrate = withStats.Min(p => p.AccountWinrate);

        var withWeighted = withStats.Where(p => p.WeightedWinrate >= 0).ToList();
        if (withWeighted.Count > 0)
        {
            result.AvgWeightedWinrate = withWeighted.Average(p => p.WeightedWinrate);
        }

        var withRecent = withStats.Where(p => p.Recent20Winrate >= 0).ToList();
        if (withRecent.Count > 0)
        {
            result.AvgRecentWinrate = withRecent.Average(p => p.Recent20Winrate);
        }

        result.UnicumCount = withStats.Count(p =>
            p.AccountWinrate > opt.UnicumWinrateThreshold
            && p.AccountBattles >= opt.UnicumBattlesThreshold);

        result.ApeCount = withStats.Count(p =>
            p.AccountWinrate >= 0 && p.AccountWinrate <= opt.ApeWinrateThreshold);

        result.AvgTier = humanPlayers.Count > 0 ? humanPlayers.Average(p => p.ShipTier) : 0;

        result.Div2Groups = humanPlayers.Count(p => p.DivisionSize == 2) / 2;
        result.Div3Groups = humanPlayers.Count(p => p.DivisionSize == 3) / 3;

        // 分舰种对位特征。舰种数量来自对局名单，不依赖快照。
        result.DdCount = humanPlayers.Count(p => p.ShipClass == ShipClass.Destroyer);
        result.CvCount = humanPlayers.Count(p => p.ShipClass == ShipClass.Carrier);

        // 收缩胜率类特征只聚合有分舰种统计的玩家，与 Python 端
        // aggregate_team 的口径一致。全队无数据时保持 0。
        var withClass = withStats.Where(p =>
            p.ClassBattles >= 0 && p.ClassWins >= 0).ToList();
        if (withClass.Count > 0)
        {
            result.AvgClassWr = withClass.Average(p =>
                ShrunkWinrate(p.ClassBattles, p.ClassWins, p.AccountWinrate));

            var ddPlayers = withClass.Where(p => p.ShipClass == ShipClass.Destroyer).ToList();
            if (ddPlayers.Count > 0)
            {
                result.DdWr = ddPlayers.Average(p =>
                    ShrunkWinrate(p.ClassBattles, p.ClassWins, p.AccountWinrate));
            }

            var cvPlayers = withClass.Where(p => p.ShipClass == ShipClass.Carrier).ToList();
            if (cvPlayers.Count > 0)
            {
                result.CvWr = cvPlayers.Average(p =>
                    ShrunkWinrate(p.ClassBattles, p.ClassWins, p.AccountWinrate));
            }
        }

        result.FreshShipCount = humanPlayers.Count(p =>
            p.ShipBattles >= 0 && p.ShipBattles < FreshShipBattles);

        return result;
    }

    /// <summary>
    /// 计算双方特征的差分。差分比绝对值更有判别力。
    /// </summary>
    public static TeamFeatureDiff ComputeDiff(TeamFeatures ally, TeamFeatures enemy)
    {
        ArgumentNullException.ThrowIfNull(ally);
        ArgumentNullException.ThrowIfNull(enemy);

        return new TeamFeatureDiff
        {
            WinrateDiff = ally.AvgAccountWinrate - enemy.AvgAccountWinrate,
            WeightedWinrateDiff = ally.AvgWeightedWinrate - enemy.AvgWeightedWinrate,
            RecentWinrateDiff = ally.AvgRecentWinrate - enemy.AvgRecentWinrate,
            BattlesDiff = ally.AvgBattles - enemy.AvgBattles,
            UnicumCountDiff = ally.UnicumCount - enemy.UnicumCount,
            ApeCountDiff = ally.ApeCount - enemy.ApeCount,
            TierAvgDiff = ally.AvgTier - enemy.AvgTier,
            PlayerCountDiff = ally.PlayerCount - enemy.PlayerCount,
        };
    }
}

/// <summary>
/// 队伍层面的聚合特征。
/// </summary>
public sealed class TeamFeatures
{
    /// <summary>非机器人玩家数。</summary>
    public int PlayerCount { get; set; }

    /// <summary>机器人数。</summary>
    public int BotCount { get; set; }

    /// <summary>隐藏战绩人数。</summary>
    public int HiddenCount { get; set; }

    /// <summary>队伍平均账号胜率。无有效数据时为 0。</summary>
    public double AvgAccountWinrate { get; set; }

    /// <summary>队伍平均加权胜率。</summary>
    public double AvgWeightedWinrate { get; set; }

    /// <summary>队伍平均近期胜率。</summary>
    public double AvgRecentWinrate { get; set; }

    /// <summary>队伍平均场次。</summary>
    public double AvgBattles { get; set; }

    /// <summary>队伍最高胜率，衡量是否有大腿。</summary>
    public double TopWinrate { get; set; }

    /// <summary>队伍最低胜率。</summary>
    public double BottomWinrate { get; set; }

    /// <summary>队伍中海王数量。</summary>
    public int UnicumCount { get; set; }

    /// <summary>队伍中猴子数量。</summary>
    public int ApeCount { get; set; }

    /// <summary>队伍平均船等级。</summary>
    public double AvgTier { get; set; }

    /// <summary>双排组数。</summary>
    public int Div2Groups { get; set; }

    /// <summary>三排组数。</summary>
    public int Div3Groups { get; set; }

    /// <summary>本局舰种的收缩胜率均值。无分舰种数据时为 0。</summary>
    public double AvgClassWr { get; set; }

    /// <summary>队伍中 DD 玩家的收缩胜率均值。无数据时为 0。</summary>
    public double DdWr { get; set; }

    /// <summary>队伍中 CV 玩家的收缩胜率均值。无数据时为 0。</summary>
    public double CvWr { get; set; }

    /// <summary>队伍中 DD 数量，来自对局名单，不依赖快照。</summary>
    public int DdCount { get; set; }

    /// <summary>队伍中 CV 数量，来自对局名单，不依赖快照。</summary>
    public int CvCount { get; set; }

    /// <summary>队伍中白板船人数，本局船只场次低于阈值。</summary>
    public int FreshShipCount { get; set; }
}

/// <summary>
/// 双方特征的差分。
/// </summary>
public sealed class TeamFeatureDiff
{
    /// <summary>平均胜率差。</summary>
    public double WinrateDiff { get; set; }

    /// <summary>平均加权胜率差。</summary>
    public double WeightedWinrateDiff { get; set; }

    /// <summary>平均近期胜率差。</summary>
    public double RecentWinrateDiff { get; set; }

    /// <summary>平均场次差。</summary>
    public double BattlesDiff { get; set; }

    /// <summary>海王数差。</summary>
    public int UnicumCountDiff { get; set; }

    /// <summary>猴子数差。</summary>
    public int ApeCountDiff { get; set; }

    /// <summary>平均船等级差。</summary>
    public double TierAvgDiff { get; set; }

    /// <summary>人数差。</summary>
    public int PlayerCountDiff { get; set; }
}

/// <summary>
/// 队伍聚合的阈值参数。
/// </summary>
public sealed record TeamAggregationOptions
{
    /// <summary>默认参数。</summary>
    public static TeamAggregationOptions Default { get; } = new();

    /// <summary>猴子胜率阈值。</summary>
    public double ApeWinrateThreshold { get; init; } = TeamAggregator.DefaultApeWinrateThreshold;

    /// <summary>海王胜率阈值。</summary>
    public double UnicumWinrateThreshold { get; init; } = TeamAggregator.DefaultUnicumWinrateThreshold;

    /// <summary>海王场次阈值。</summary>
    public double UnicumBattlesThreshold { get; init; } = TeamAggregator.DefaultUnicumBattlesThreshold;
}
