using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Core.Tests.Scoring;

public class RecentFormCalculatorTests
{
    private static PlayerBattleHistoryEntry Entry(
        bool won,
        int hoursAgo,
        int damage = 10000,
        int divisionSize = 1)
    {
        return new PlayerBattleHistoryEntry
        {
            BattleId = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.UtcNow.AddHours(-hoursAgo),
            Won = won,
            DamageDealt = damage,
            ShipId = "TEST",
            DivisionSize = divisionSize,
        };
    }

    [Fact]
    public void Compute_EnoughSamples_ProducesRecentWinrate()
    {
        // 最近 20 场里 12 胜 8 负。
        var history = new List<PlayerBattleHistoryEntry>();
        for (int i = 0; i < 12; i++)
        {
            history.Add(Entry(won: true, hoursAgo: i));
        }
        for (int i = 12; i < 20; i++)
        {
            history.Add(Entry(won: false, hoursAgo: i));
        }

        var form = RecentFormCalculator.Compute(history);

        Assert.True(form.HasEnoughSamples);
        Assert.Equal(20, form.RecentBattles);
        Assert.Equal(0.60, form.RecentWinrate, precision: 6);
    }

    [Fact]
    public void Compute_TooFewSamples_ReturnsSentinel()
    {
        var history = new List<PlayerBattleHistoryEntry>
        {
            Entry(true, 1),
            Entry(false, 2),
        };

        var form = RecentFormCalculator.Compute(history);

        Assert.False(form.HasEnoughSamples);
        Assert.Equal(-1, form.RecentWinrate);
    }

    [Fact]
    public void Compute_EmptyHistory_ReturnsSentinel()
    {
        var form = RecentFormCalculator.Compute([]);

        Assert.False(form.HasEnoughSamples);
        Assert.Equal(-1, form.RecentWinrate);
    }

    [Fact]
    public void Compute_ShortWindowCapsAtWindowSize()
    {
        // 给 50 场历史，短窗口只取 20 场。
        var history = new List<PlayerBattleHistoryEntry>();
        for (int i = 0; i < 50; i++)
        {
            history.Add(Entry(won: i < 10, hoursAgo: i));
        }

        var form = RecentFormCalculator.Compute(history);

        // 最近 20 场里有 10 胜（前 10 场）。
        Assert.Equal(20, form.RecentBattles);
        Assert.Equal(0.50, form.RecentWinrate, precision: 6);
    }

    [Fact]
    public void Compute_ConsecutiveLosses_CountsFromLatest()
    {
        var history = new List<PlayerBattleHistoryEntry>
        {
            Entry(false, 1),
            Entry(false, 2),
            Entry(false, 3),
            Entry(true, 4),
            Entry(false, 5),
            Entry(false, 6),
        };

        var form = RecentFormCalculator.Compute(history);

        // 最近连续 3 场失败。
        Assert.Equal(3, form.ConsecutiveLosses);
    }

    [Fact]
    public void Compute_ConsecutiveLosses_ZeroWhenLatestWon()
    {
        var history = new List<PlayerBattleHistoryEntry>
        {
            Entry(true, 1),
            Entry(false, 2),
            Entry(false, 3),
            Entry(false, 4),
            Entry(false, 5),
        };

        var form = RecentFormCalculator.Compute(history);

        Assert.Equal(0, form.ConsecutiveLosses);
    }

    [Fact]
    public void Compute_WinrateTrend_PositiveWhenImproving()
    {
        // 最近 5 场全胜，之前 15 场全负，短窗口 20 场胜率 25%，
        // 长窗口 50 场同样 25%，趋势接近零。构造更明显的对比。
        var history = new List<PlayerBattleHistoryEntry>();
        // 最近 20 场：15 胜 5 负，胜率 75%
        for (int i = 0; i < 15; i++)
        {
            history.Add(Entry(won: true, hoursAgo: i));
        }
        for (int i = 15; i < 20; i++)
        {
            history.Add(Entry(won: false, hoursAgo: i));
        }
        // 再往前 30 场：10 胜 20 负，把长窗口胜率拉低。
        for (int i = 20; i < 30; i++)
        {
            history.Add(Entry(won: true, hoursAgo: i));
        }
        for (int i = 30; i < 50; i++)
        {
            history.Add(Entry(won: false, hoursAgo: i));
        }

        var form = RecentFormCalculator.Compute(history);

        // 短窗口 75%，长窗口 25/50 = 50%，趋势为正。
        Assert.Equal(0.75, form.RecentWinrate, precision: 6);
        Assert.Equal(0.50, form.LongerWinrate, precision: 6);
        Assert.True(form.WinrateTrend > 0);
    }

    [Fact]
    public void Compute_AverageDamage()
    {
        var history = new List<PlayerBattleHistoryEntry>();
        for (int i = 0; i < 10; i++)
        {
            history.Add(Entry(won: true, hoursAgo: i, damage: 20000));
        }

        var form = RecentFormCalculator.Compute(history);

        Assert.Equal(20000, form.RecentAvgDamage, precision: 6);
    }

    [Fact]
    public void ComputeSession_SplitsOnTimeGap()
    {
        // 最近 3 场在 2 小时内，再往前隔了很久。
        var history = new List<PlayerBattleHistoryEntry>
        {
            Entry(true, 1),
            Entry(false, 1),
            Entry(true, 2),
            Entry(true, 10),  // 隔了 8 小时，新会话
            Entry(false, 11),
        };

        var session = RecentFormCalculator.ComputeSession(history);

        Assert.Equal(3, session.SessionBattles);
        Assert.Equal(2.0 / 3.0, session.SessionWinrate, precision: 6);
    }

    [Fact]
    public void ComputeSession_EmptyHistory_ReturnsZero()
    {
        var session = RecentFormCalculator.ComputeSession([]);

        Assert.Equal(0, session.SessionBattles);
        Assert.Equal(-1, session.SessionWinrate);
    }

    [Fact]
    public void ComputeSession_SingleBattle()
    {
        var history = new List<PlayerBattleHistoryEntry> { Entry(true, 1) };

        var session = RecentFormCalculator.ComputeSession(history);

        Assert.Equal(1, session.SessionBattles);
        Assert.Equal(1.0, session.SessionWinrate, precision: 6);
    }
}
