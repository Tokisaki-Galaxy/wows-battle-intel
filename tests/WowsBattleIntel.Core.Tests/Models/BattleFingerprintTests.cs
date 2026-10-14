using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Tests;

public class BattleFingerprintTests
{
    private static PlayerRecord Player(string accountId, bool isAlly = true) =>
        new() { AccountId = accountId, IsAlly = isAlly };

    private static BattleRecord MakeBattle(
        string battleId,
        DateTimeOffset startedAt,
        string mapId = "spaces/50_Gold_harbor",
        params PlayerRecord[] players)
    {
        return new BattleRecord
        {
            BattleId = battleId,
            StartedAt = startedAt,
            MapId = mapId,
            BattleType = BattleType.Random,
            Players = players.ToList(),
        };
    }

    private static readonly DateTimeOffset Base = DateTimeOffset.FromUnixTimeMilliseconds(1753163342000);

    [Fact]
    public void PlayerSignature_IgnoresOrderAndDuplicates()
    {
        string a = BattleFingerprint.PlayerSignature([Player("1001"), Player("2002")]);
        string b = BattleFingerprint.PlayerSignature([Player("2002"), Player("1001")]);

        Assert.Equal(a, b);
    }

    [Fact]
    public void PlayerSignature_DiffersForDifferentRosters()
    {
        string a = BattleFingerprint.PlayerSignature([Player("1001"), Player("2002")]);
        string b = BattleFingerprint.PlayerSignature([Player("1001"), Player("3003")]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void IsEquivalentTo_SameRosterMapWithinWindow_ReturnsTrue()
    {
        var left = MakeBattle("a", Base, "spaces/50_Gold_harbor", Player("1001"), Player("2002"));
        // 同一局另一名玩家的回放，开始时间晚 5 分钟，battle_id 不同。
        var right = MakeBattle("b", Base.AddMinutes(5), "spaces/50_Gold_harbor", Player("1001"), Player("2002"));

        Assert.True(BattleFingerprint.IsEquivalentTo(left, right));
    }

    [Fact]
    public void IsEquivalentTo_BeyondWindow_ReturnsFalse()
    {
        var left = MakeBattle("a", Base, "spaces/50_Gold_harbor", Player("1001"), Player("2002"));
        var right = MakeBattle("b", Base.AddSeconds(800), "spaces/50_Gold_harbor", Player("1001"), Player("2002"));

        Assert.False(BattleFingerprint.IsEquivalentTo(left, right));
    }

    [Fact]
    public void IsEquivalentTo_DifferentMap_ReturnsFalse()
    {
        var left = MakeBattle("a", Base, "spaces/50_Gold_harbor", Player("1001"), Player("2002"));
        var right = MakeBattle("b", Base, "spaces/17_NI_farmer", Player("1001"), Player("2002"));

        Assert.False(BattleFingerprint.IsEquivalentTo(left, right));
    }

    [Fact]
    public void IsEquivalentTo_DifferentRoster_ReturnsFalse()
    {
        var left = MakeBattle("a", Base, "spaces/50_Gold_harbor", Player("1001"), Player("2002"));
        var right = MakeBattle("b", Base, "spaces/50_Gold_harbor", Player("1001"), Player("3003"));

        Assert.False(BattleFingerprint.IsEquivalentTo(left, right));
    }
}

