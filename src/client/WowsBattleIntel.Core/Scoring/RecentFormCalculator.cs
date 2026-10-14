using WowsBattleIntel.Core.Abstractions;

namespace WowsBattleIntel.Core.Scoring;

/// <summary>
/// 近期状态特征计算。
/// </summary>
/// <remarks>
/// 账号总胜率是滞后指标，两万场的老号总胜率早已固化，反映不出这两周是
/// 状态火热还是连败上头。近期状态用最近若干场的表现捕捉短期波动。
///
/// 数据来源是已积累的逐场对局历史，不是 WG API。每场对局都有结果，
/// 玩家所在队伍的结果即他的胜负，所以近期胜率可以本地算出。关键约束：
/// 只能用对局开始之前的历史，否则构成时间泄漏。
/// </remarks>
public static class RecentFormCalculator
{
    /// <summary>短窗口场次，默认 20 场。</summary>
    public const int DefaultShortWindow = 20;

    /// <summary>长窗口场次，默认 50 场。</summary>
    public const int DefaultLongWindow = 50;

    /// <summary>样本不足时该窗口不产出特征。</summary>
    public const int MinimumSamples = 5;

    /// <summary>
    /// 从历史记录计算近期状态。
    /// </summary>
    /// <param name="history">该玩家在预测时点之前的逐场记录，按时间倒序。</param>
    /// <param name="shortWindow">短窗口场次。</param>
    /// <param name="longWindow">长窗口场次。</param>
    /// <returns>近期状态特征。样本不足时字段保持 -1 哨兵。</returns>
    public static RecentForm Compute(
        IReadOnlyList<PlayerBattleHistoryEntry> history,
        int shortWindow = DefaultShortWindow,
        int longWindow = DefaultLongWindow)
    {
        ArgumentNullException.ThrowIfNull(history);

        var result = new RecentForm();

        if (history.Count >= MinimumSamples)
        {
            int take = Math.Min(shortWindow, history.Count);
            var recent = history.Take(take).ToList();

            result.RecentWinrate = recent.Count(x => x.Won) / (double)recent.Count;
            result.RecentBattles = recent.Count;

            var withDamage = recent.Where(x => x.DamageDealt >= 0).ToList();
            if (withDamage.Count > 0)
            {
                result.RecentAvgDamage = withDamage.Average(x => x.DamageDealt);
            }
        }

        if (history.Count >= MinimumSamples)
        {
            int take = Math.Min(longWindow, history.Count);
            var longer = history.Take(take).ToList();
            result.LongerWinrate = longer.Count(x => x.Won) / (double)longer.Count;
        }

        // 状态趋势：短窗口胜率与长窗口胜率的差。正值表示状态上行。
        if (result.RecentWinrate >= 0 && result.LongerWinrate >= 0)
        {
            result.WinrateTrend = result.RecentWinrate - result.LongerWinrate;
        }

        // 当前连续失败次数，捕捉连败上头。从最近一场往前数。
        int losses = 0;
        foreach (var entry in history)
        {
            if (entry.Won)
            {
                break;
            }

            losses++;
        }

        result.ConsecutiveLosses = losses;

        return result;
    }

    /// <summary>
    /// 计算会话状态：以时间间隔切分连续游玩，统计当前会话的表现。
    /// </summary>
    /// <param name="history">历史记录，按时间倒序。</param>
    /// <param name="sessionGap">会话间隔阈值，超过则视为新会话。</param>
    public static SessionState ComputeSession(
        IReadOnlyList<PlayerBattleHistoryEntry> history,
        TimeSpan? sessionGap = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        var gap = sessionGap ?? TimeSpan.FromHours(2);

        var result = new SessionState();
        if (history.Count == 0)
        {
            return result;
        }

        // 从最近一场往前，直到时间间隔超过阈值。
        var session = new List<PlayerBattleHistoryEntry>();
        DateTimeOffset previous = history[0].StartedAt;
        session.Add(history[0]);

        for (int i = 1; i < history.Count; i++)
        {
            TimeSpan delta = previous - history[i].StartedAt;
            if (delta > gap)
            {
                break;
            }

            session.Add(history[i]);
            previous = history[i].StartedAt;
        }

        result.SessionBattles = session.Count;
        result.SessionWinrate = session.Count(x => x.Won) / (double)session.Count;
        result.SessionStart = session[^1].StartedAt;
        return result;
    }
}

/// <summary>
/// 近期状态特征。
/// </summary>
public sealed class RecentForm
{
    /// <summary>最近短窗口胜率，-1 表示样本不足。</summary>
    public double RecentWinrate { get; set; } = -1;

    /// <summary>短窗口实际场次。</summary>
    public int RecentBattles { get; set; }

    /// <summary>短窗口场均伤害，-1 表示未知。</summary>
    public double RecentAvgDamage { get; set; } = -1;

    /// <summary>较长窗口胜率，-1 表示样本不足。</summary>
    public double LongerWinrate { get; set; } = -1;

    /// <summary>短窗口相对长窗口的胜率差，正值表示状态上行。</summary>
    public double WinrateTrend { get; set; } = -1;

    /// <summary>当前连续失败次数。</summary>
    public int ConsecutiveLosses { get; set; }

    /// <summary>是否有足够样本产出特征。</summary>
    public bool HasEnoughSamples => RecentWinrate >= 0;
}

/// <summary>
/// 会话状态特征。
/// </summary>
public sealed class SessionState
{
    /// <summary>本次会话已打场次。</summary>
    public int SessionBattles { get; set; }

    /// <summary>本次会话胜率，-1 表示未知。</summary>
    public double SessionWinrate { get; set; } = -1;

    /// <summary>本次会话起始时间。</summary>
    public DateTimeOffset SessionStart { get; set; }
}
