using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Core.Tests.Scoring;

public class WeightedWinrateTests
{
    [Fact]
    public void Compute_WithKnownInputs_MatchesApeRadarFormula()
    {
        // 手工按 ApeRadar 公式推算：
        // 单人 1000 场 50% -> 权重 5000
        // 双排 500 场 60%  -> 权重 1000
        // 三排 100 场 70%  -> 权重 100
        // 账号加权 = (0.5*5000 + 0.6*1000 + 0.7*100)/6100
        double expectedAccount =
            (0.5 * 5000 + 0.6 * 1000 + 0.7 * 100) / 6100.0;

        // 单船 60 场达到最大权重 20%，单船胜率 40%
        double shipWeight = 0.20;
        double expected = expectedAccount * (1 - shipWeight) + 0.40 * shipWeight;

        double actual = WeightedWinrate.Compute(
            accountWinrateSolo: 0.5, accountBattlesSolo: 1000,
            accountWinrateDiv2: 0.6, accountBattlesDiv2: 500,
            accountWinrateDiv3: 0.7, accountBattlesDiv3: 100,
            shipWinrate: 0.40, shipBattles: 60);

        Assert.Equal(expected, actual, precision: 10);
    }

    [Fact]
    public void Compute_ShipBattlesBelowMaxWeight_ScalesLinearly()
    {
        // 单船 30 场，权重应为 20% * 30/60 = 10%
        double accountOnly = WeightedWinrate.Compute(
            accountWinrateSolo: 0.5, accountBattlesSolo: 100,
            accountWinrateDiv2: 0, accountBattlesDiv2: 0,
            accountWinrateDiv3: 0, accountBattlesDiv3: 0,
            shipWinrate: 0.5, shipBattles: 0);

        double withShip = WeightedWinrate.Compute(
            accountWinrateSolo: 0.5, accountBattlesSolo: 100,
            accountWinrateDiv2: 0, accountBattlesDiv2: 0,
            accountWinrateDiv3: 0, accountBattlesDiv3: 0,
            shipWinrate: 1.0, shipBattles: 30);

        // 账号胜率 0.5，单船胜率 1.0，权重 10%，结果应为 0.55
        Assert.Equal(0.5, accountOnly, precision: 10);
        Assert.Equal(0.55, withShip, precision: 10);
    }

    [Fact]
    public void Compute_AllZeroBattles_ReturnsZero()
    {
        double actual = WeightedWinrate.Compute(0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, actual);
    }

    [Fact]
    public void ComputeFromPlayer_FallsBackToAccountWinrate_WhenNoBreakdown()
    {
        var player = new Models.PlayerRecord
        {
            AccountWinrate = 0.55,
            AccountBattles = 1000,
            // 单人、双排、三排与单船全部保持哨兵值 -1
        };

        double actual = WeightedWinrate.Compute(player);

        Assert.Equal(0.55, actual, precision: 10);
    }

    [Fact]
    public void ComputeFromPlayer_IgnoresNegativeSentinels()
    {
        var player = new Models.PlayerRecord
        {
            AccountWinrate = 0.55,
            AccountWinrateSolo = 0.60,
            AccountBattlesSolo = 100,
            AccountWinrateDiv2 = -1,   // 未知
            AccountBattlesDiv2 = -1,   // 未知
            AccountWinrateDiv3 = -1,
            AccountBattlesDiv3 = -1,
            ShipWinrate = 0.40,
            ShipBattles = 60,
        };

        // 只用单人数据：账号加权 = 0.60，单船权重 20%
        double expected = 0.60 * 0.80 + 0.40 * 0.20;
        double actual = WeightedWinrate.Compute(player);

        Assert.Equal(expected, actual, precision: 10);
    }
}
