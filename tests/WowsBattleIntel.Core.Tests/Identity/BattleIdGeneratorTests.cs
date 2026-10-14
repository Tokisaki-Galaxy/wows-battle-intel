using WowsBattleIntel.Core.Identity;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Tests.Identity;

public class BattleIdGeneratorTests
{
    private static PlayerRecord MakePlayer(string id, GameServer server)
    {
        return new PlayerRecord { AccountId = id, Server = server };
    }

    [Fact]
    public void Generate_SameInputs_ProducesSameId()
    {
        var startedAt = new DateTimeOffset(2026, 7, 22, 12, 43, 8, TimeSpan.Zero);
        var players = new[]
        {
            MakePlayer("100", GameServer.Asia),
            MakePlayer("200", GameServer.Asia),
        };

        string id1 = BattleIdGenerator.Generate(startedAt, "map_a", BattleType.Random, players);
        string id2 = BattleIdGenerator.Generate(startedAt, "map_a", BattleType.Random, players);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Generate_PlayerOrderDiffers_ProducesSameId()
    {
        var startedAt = new DateTimeOffset(2026, 7, 22, 12, 43, 8, TimeSpan.Zero);
        var playersA = new[]
        {
            MakePlayer("100", GameServer.Asia),
            MakePlayer("200", GameServer.Asia),
        };
        var playersB = new[]
        {
            MakePlayer("200", GameServer.Asia),
            MakePlayer("100", GameServer.Asia),
        };

        string id1 = BattleIdGenerator.Generate(startedAt, "map_a", BattleType.Random, playersA);
        string id2 = BattleIdGenerator.Generate(startedAt, "map_a", BattleType.Random, playersB);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Generate_MillisecondDifference_ProducesSameId()
    {
        // 毫秒差异不应改变对局标识，避免同一局被算成两局。
        var baseTime = new DateTimeOffset(2026, 7, 22, 12, 43, 8, TimeSpan.Zero);
        var players = new[] { MakePlayer("100", GameServer.Asia) };

        string id1 = BattleIdGenerator.Generate(baseTime, "map_a", BattleType.Random, players);
        string id2 = BattleIdGenerator.Generate(baseTime.AddMilliseconds(500), "map_a", BattleType.Random, players);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Generate_DifferentPlayers_ProducesDifferentId()
    {
        var startedAt = new DateTimeOffset(2026, 7, 22, 12, 43, 8, TimeSpan.Zero);

        string id1 = BattleIdGenerator.Generate(
            startedAt, "map_a", BattleType.Random,
            [MakePlayer("100", GameServer.Asia)]);
        string id2 = BattleIdGenerator.Generate(
            startedAt, "map_a", BattleType.Random,
            [MakePlayer("999", GameServer.Asia)]);

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void Generate_DifferentServer_ProducesDifferentId()
    {
        // 相同账号 ID 在不同服务器是不同的人。
        var startedAt = new DateTimeOffset(2026, 7, 22, 12, 43, 8, TimeSpan.Zero);

        string id1 = BattleIdGenerator.Generate(
            startedAt, "map_a", BattleType.Random,
            [MakePlayer("100", GameServer.Asia)]);
        string id2 = BattleIdGenerator.Generate(
            startedAt, "map_a", BattleType.Random,
            [MakePlayer("100", GameServer.Eu)]);

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void Generate_IgnoresPlayersWithoutAccountId()
    {
        var startedAt = new DateTimeOffset(2026, 7, 22, 12, 43, 8, TimeSpan.Zero);
        var withUnknown = new[]
        {
            MakePlayer("100", GameServer.Asia),
            MakePlayer("-1", GameServer.Asia),
        };
        var withoutUnknown = new[] { MakePlayer("100", GameServer.Asia) };

        string id1 = BattleIdGenerator.Generate(startedAt, "map_a", BattleType.Random, withUnknown);
        string id2 = BattleIdGenerator.Generate(startedAt, "map_a", BattleType.Random, withoutUnknown);

        Assert.Equal(id1, id2);
    }
}
