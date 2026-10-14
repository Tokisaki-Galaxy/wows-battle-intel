using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Core.Tests.Scoring;

public class TeamAggregatorTests
{
    private static PlayerRecord MakePlayer(
        string id,
        bool isAlly,
        double winrate,
        double battles,
        bool hidden = false,
        bool isBot = false,
        int divisionSize = 1,
        int tier = 10)
    {
        return new PlayerRecord
        {
            AccountId = id,
            IsAlly = isAlly,
            AccountWinrate = hidden ? -1 : winrate,
            AccountBattles = hidden ? -1 : battles,
            IsHidden = hidden,
            IsBot = isBot,
            DivisionSize = divisionSize,
            ShipTier = tier,
        };
    }

    [Fact]
    public void Aggregate_ComputesAveragesAndCounts()
    {
        var players = new[]
        {
            MakePlayer("1", true, 0.65, 1000),  // 海王
            MakePlayer("2", true, 0.45, 800),   // 猴子
            MakePlayer("3", true, 0.55, 600),
        };

        var result = TeamAggregator.Aggregate(players);

        Assert.Equal(3, result.PlayerCount);
        Assert.Equal(0.65, result.TopWinrate, precision: 10);
        Assert.Equal(0.45, result.BottomWinrate, precision: 10);
        Assert.Equal(1, result.UnicumCount);  // 只有 0.65 且场次 >= 500
        Assert.Equal(1, result.ApeCount);     // 只有 0.45 <= 0.47
    }

    [Fact]
    public void Aggregate_ExcludesHiddenPlayersFromStats()
    {
        var players = new[]
        {
            MakePlayer("1", true, 0.65, 1000),
            MakePlayer("2", true, 0.0, 0, hidden: true),
        };

        var result = TeamAggregator.Aggregate(players);

        // 隐藏玩家计入人数，但不参与胜率统计
        Assert.Equal(2, result.PlayerCount);
        Assert.Equal(1, result.HiddenCount);
        Assert.Equal(0.65, result.AvgAccountWinrate, precision: 10);
    }

    [Fact]
    public void Aggregate_ExcludesBotsFromHumanCount()
    {
        var players = new[]
        {
            MakePlayer("1", true, 0.55, 500),
            MakePlayer("bot1", true, 0, 0, isBot: true),
            MakePlayer("bot2", true, 0, 0, isBot: true),
        };

        var result = TeamAggregator.Aggregate(players);

        Assert.Equal(1, result.PlayerCount);
        Assert.Equal(2, result.BotCount);
    }

    [Fact]
    public void Aggregate_AllHidden_ReturnsZeroStats()
    {
        var players = new[]
        {
            MakePlayer("1", true, 0, 0, hidden: true),
            MakePlayer("2", true, 0, 0, hidden: true),
        };

        var result = TeamAggregator.Aggregate(players);

        Assert.Equal(2, result.PlayerCount);
        Assert.Equal(0, result.AvgAccountWinrate);
        Assert.Equal(0, result.UnicumCount);
        Assert.Equal(0, result.ApeCount);
    }

    [Fact]
    public void Aggregate_CountsDivisionGroups()
    {
        var players = new[]
        {
            MakePlayer("1", true, 0.55, 500, divisionSize: 3),
            MakePlayer("2", true, 0.55, 500, divisionSize: 3),
            MakePlayer("3", true, 0.55, 500, divisionSize: 3),
            MakePlayer("4", true, 0.55, 500, divisionSize: 2),
            MakePlayer("5", true, 0.55, 500, divisionSize: 2),
            MakePlayer("6", true, 0.55, 500, divisionSize: 1),
        };

        var result = TeamAggregator.Aggregate(players);

        Assert.Equal(1, result.Div3Groups);
        Assert.Equal(1, result.Div2Groups);
    }

    [Fact]
    public void ComputeDiff_ProducesSignedDifferences()
    {
        var ally = new TeamFeatures { AvgAccountWinrate = 0.55, UnicumCount = 2, ApeCount = 1 };
        var enemy = new TeamFeatures { AvgAccountWinrate = 0.50, UnicumCount = 1, ApeCount = 3 };

        var diff = TeamAggregator.ComputeDiff(ally, enemy);

        Assert.Equal(0.05, diff.WinrateDiff, precision: 10);
        Assert.Equal(1, diff.UnicumCountDiff);
        Assert.Equal(-2, diff.ApeCountDiff);
    }
}
