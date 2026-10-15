using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Core.Tests.Scoring;

public class SnapshotFeatureBuilderTests
{
    private static PlayerRecord MakePlayer(string accountId, bool isAlly = true)
    {
        return new PlayerRecord
        {
            AccountId = accountId,
            PlayerName = $"Player{accountId}",
            Server = GameServer.Asia,
            IsAlly = isAlly,
            ShipId = "TEST001",
            ShipName = "TestShip",
            ShipTier = 10,
            ShipClass = ShipClass.Battleship,
            DivisionSize = 2,
            DamageDealt = 50000,
            Kills = 2,
            Survived = true,
            // 账号层特征保持哨兵，等待快照补齐。
            AccountWinrate = -1,
        };
    }

    private static PlayerSnapshot MakeSnapshot(string accountId, double winrate)
    {
        return new PlayerSnapshot
        {
            AccountId = accountId,
            Server = GameServer.Asia,
            CapturedAt = DateTimeOffset.UtcNow,
            AccountBattles = 5000,
            AccountWinrate = winrate,
            AccountWinrateSolo = winrate + 0.01,
            AccountBattlesSolo = 3000,
            AccountWinrateDiv2 = winrate - 0.01,
            AccountBattlesDiv2 = 1500,
            AccountWinrateDiv3 = winrate,
            AccountBattlesDiv3 = 500,
            WeightedWinrate = winrate,
        };
    }

    [Fact]
    public void Enrich_FillsAccountStatsFromSnapshot()
    {
        var battle = new BattleRecord
        {
            BattleId = "test",
            Players = [MakePlayer("1001")],
        };

        var snapshots = new Dictionary<string, PlayerSnapshot>
        {
            ["Asia:1001"] = MakeSnapshot("1001", 0.58),
        };

        var enriched = SnapshotFeatureBuilder.Enrich(
            battle,
            (id, server, _) => snapshots.GetValueOrDefault($"{server}:{id}"));

        var player = enriched.Players[0];
        Assert.Equal(0.58, player.AccountWinrate, precision: 6);
        Assert.Equal(5000, player.AccountBattles);
        Assert.True(player.HasStats);
    }

    [Fact]
    public void Enrich_FillsAccountAvgDamageFromSnapshot()
    {
        var battle = new BattleRecord
        {
            BattleId = "test",
            Players = [MakePlayer("1001")],
        };

        var snapshot = MakeSnapshot("1001", 0.58);
        snapshot.AccountAvgDamage = 61700;
        var snapshots = new Dictionary<string, PlayerSnapshot>
        {
            ["Asia:1001"] = snapshot,
        };

        var enriched = SnapshotFeatureBuilder.Enrich(
            battle,
            (id, server, _) => snapshots.GetValueOrDefault($"{server}:{id}"));

        Assert.Equal(61700, enriched.Players[0].AccountAvgDamage, precision: 6);
    }

    [Fact]
    public void Enrich_FillsShipStatsWhenShipIdMatches()
    {
        var battle = new BattleRecord
        {
            BattleId = "test",
            Players = [MakePlayer("1001")],
        };

        var snapshot = MakeSnapshot("1001", 0.58);
        snapshot.ShipId = "TEST001";
        snapshot.ShipBattles = 230;
        snapshot.ShipWinrate = 0.62;
        snapshot.ShipAvgDamage = 71200;
        var snapshots = new Dictionary<string, PlayerSnapshot>
        {
            ["Asia:1001"] = snapshot,
        };

        var enriched = SnapshotFeatureBuilder.Enrich(
            battle,
            (id, server, _) => snapshots.GetValueOrDefault($"{server}:{id}"));

        var player = enriched.Players[0];
        Assert.Equal(230, player.ShipBattles);
        Assert.Equal(0.62, player.ShipWinrate, precision: 6);
        Assert.Equal(71200, player.ShipAvgDamage, precision: 6);
    }

    [Fact]
    public void Enrich_KeepsPlayerShipStatsWhenShipIdDiffers()
    {
        // 账号级快照的单船数据属于另一艘船，不得覆盖本局船只的原值。
        var battle = new BattleRecord
        {
            BattleId = "test",
            Players = [MakePlayer("1001")],
        };
        battle.Players[0].ShipBattles = 100;
        battle.Players[0].ShipWinrate = 0.55;
        battle.Players[0].ShipAvgDamage = 50000;

        var snapshot = MakeSnapshot("1001", 0.58);
        snapshot.ShipId = "OTHER";
        snapshot.ShipBattles = 999;
        snapshot.ShipWinrate = 0.9;
        snapshot.ShipAvgDamage = 99999;
        var snapshots = new Dictionary<string, PlayerSnapshot>
        {
            ["Asia:1001"] = snapshot,
        };

        var enriched = SnapshotFeatureBuilder.Enrich(
            battle,
            (id, server, _) => snapshots.GetValueOrDefault($"{server}:{id}"));

        var player = enriched.Players[0];
        Assert.Equal(100, player.ShipBattles);
        Assert.Equal(0.55, player.ShipWinrate, precision: 6);
        Assert.Equal(50000, player.ShipAvgDamage, precision: 6);
    }

    [Fact]
    public void Enrich_ShipSnapshotHitsIndependentlyOfAccountSnapshot()
    {
        // 账号级最新快照是船 B 的，本局开船 A：账号层用最新快照，
        // 单船层按船命中历史快照，两层独立。
        var battle = new BattleRecord
        {
            BattleId = "test",
            Players = [MakePlayer("1001")],
        };

        var shipASnapshot = MakeSnapshot("1001", 0.51);
        shipASnapshot.ShipId = "TEST001";
        shipASnapshot.ShipBattles = 100;
        shipASnapshot.ShipWinrate = 0.55;
        shipASnapshot.ShipAvgDamage = 50000;

        var shipBSnapshot = MakeSnapshot("1001", 0.58);
        shipBSnapshot.ShipId = "SHIP-B";
        shipBSnapshot.ShipBattles = 20;
        shipBSnapshot.ShipWinrate = 0.4;
        shipBSnapshot.ShipAvgDamage = 30000;

        var accountSnapshots = new Dictionary<string, PlayerSnapshot>
        {
            ["Asia:1001"] = shipBSnapshot,
        };
        var shipSnapshots = new Dictionary<string, PlayerSnapshot>
        {
            ["Asia:1001:TEST001"] = shipASnapshot,
        };

        var enriched = SnapshotFeatureBuilder.Enrich(
            battle,
            (id, server, shipId) => string.IsNullOrEmpty(shipId)
                ? accountSnapshots.GetValueOrDefault($"{server}:{id}")
                : shipSnapshots.GetValueOrDefault($"{server}:{id}:{shipId}"));

        var player = enriched.Players[0];
        // 账号层来自最新快照。
        Assert.Equal(0.58, player.AccountWinrate, precision: 6);
        Assert.Equal(5000, player.AccountBattles);
        // 单船层来自船 A 的历史快照。
        Assert.Equal(100, player.ShipBattles);
        Assert.Equal(0.55, player.ShipWinrate, precision: 6);
        Assert.Equal(50000, player.ShipAvgDamage, precision: 6);
    }

    [Fact]
    public void Enrich_ShipSnapshotFallback_WhenAccountSnapshotMissing()
    {
        // 账号层查询落空时退回船级快照，它的行里同样带账号层字段。
        var battle = new BattleRecord
        {
            BattleId = "test",
            Players = [MakePlayer("1001")],
        };

        var shipSnapshot = MakeSnapshot("1001", 0.52);
        shipSnapshot.ShipId = "TEST001";
        shipSnapshot.ShipBattles = 100;
        shipSnapshot.ShipWinrate = 0.55;
        shipSnapshot.ShipAvgDamage = 50000;

        var shipSnapshots = new Dictionary<string, PlayerSnapshot>
        {
            ["Asia:1001:TEST001"] = shipSnapshot,
        };

        var enriched = SnapshotFeatureBuilder.Enrich(
            battle,
            (id, server, shipId) => string.IsNullOrEmpty(shipId)
                ? null
                : shipSnapshots.GetValueOrDefault($"{server}:{id}:{shipId}"));

        var player = enriched.Players[0];
        Assert.Equal(0.52, player.AccountWinrate, precision: 6);
        Assert.Equal(100, player.ShipBattles);
    }

    [Fact]
    public void Enrich_PreservesBattlePerformance()
    {
        var battle = new BattleRecord
        {
            BattleId = "test",
            Players = [MakePlayer("1001")],
        };

        var snapshots = new Dictionary<string, PlayerSnapshot>
        {
            ["Asia:1001"] = MakeSnapshot("1001", 0.58),
        };

        var enriched = SnapshotFeatureBuilder.Enrich(
            battle,
            (id, server, _) => snapshots.GetValueOrDefault($"{server}:{id}"));

        var player = enriched.Players[0];
        // 单局表现应保持回放原值，不被快照覆盖。
        Assert.Equal(50000, player.DamageDealt);
        Assert.Equal(2, player.Kills);
        Assert.True(player.Survived);
        Assert.Equal(2, player.DivisionSize);
        Assert.Equal(ShipClass.Battleship, player.ShipClass);
    }

    [Fact]
    public void Enrich_MissingSnapshot_KeepsSentinel()
    {
        var battle = new BattleRecord
        {
            BattleId = "test",
            Players = [MakePlayer("9999")],
        };

        var enriched = SnapshotFeatureBuilder.Enrich(
            battle,
            (_, _, _) => null);

        var player = enriched.Players[0];
        Assert.Equal(-1, player.AccountWinrate);
        Assert.False(player.HasStats);
    }

    [Fact]
    public void Enrich_DoesNotMutateOriginal()
    {
        var original = MakePlayer("1001");
        var battle = new BattleRecord
        {
            BattleId = "test",
            Players = [original],
        };

        var snapshots = new Dictionary<string, PlayerSnapshot>
        {
            ["Asia:1001"] = MakeSnapshot("1001", 0.58),
        };

        SnapshotFeatureBuilder.Enrich(
            battle,
            (id, server, _) => snapshots.GetValueOrDefault($"{server}:{id}"));

        // 原对象不应被改动。
        Assert.Equal(-1, original.AccountWinrate);
    }

    [Fact]
    public void Enrich_PreservesBattleMetadata()
    {
        var battle = new BattleRecord
        {
            BattleId = "battle-123",
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(1753163342000),
            MapId = "Haven",
            BattleType = BattleType.Random,
            GameVersion = "15.6.0",
            Server = GameServer.Asia,
            Outcome = BattleOutcome.Win,
            DurationSeconds = 918,
            Players = [MakePlayer("1001")],
        };

        var enriched = SnapshotFeatureBuilder.Enrich(battle, (_, _, _) => null);

        Assert.Equal("battle-123", enriched.BattleId);
        Assert.Equal("Haven", enriched.MapId);
        Assert.Equal(BattleType.Random, enriched.BattleType);
        Assert.Equal(BattleOutcome.Win, enriched.Outcome);
        Assert.Equal(918, enriched.DurationSeconds);
    }

    [Fact]
    public void Enrich_MultiplePlayers_EachGetsOwnSnapshot()
    {
        var battle = new BattleRecord
        {
            BattleId = "test",
            Players =
            [
                MakePlayer("1001", isAlly: true),
                MakePlayer("2001", isAlly: false),
            ],
        };

        var snapshots = new Dictionary<string, PlayerSnapshot>
        {
            ["Asia:1001"] = MakeSnapshot("1001", 0.60),
            ["Asia:2001"] = MakeSnapshot("2001", 0.45),
        };

        var enriched = SnapshotFeatureBuilder.Enrich(
            battle,
            (id, server, _) => snapshots.GetValueOrDefault($"{server}:{id}"));

        Assert.Equal(0.60, enriched.Players[0].AccountWinrate, precision: 6);
        Assert.Equal(0.45, enriched.Players[1].AccountWinrate, precision: 6);
    }

    [Fact]
    public void Enrich_ThenAggregate_ProducesTeamFeatures()
    {
        // 端到端：补齐快照后应能算出有意义的队伍特征。
        var battle = new BattleRecord
        {
            BattleId = "test",
            Players =
            [
                MakePlayer("1001", isAlly: true),
                MakePlayer("1002", isAlly: true),
                MakePlayer("2001", isAlly: false),
                MakePlayer("2002", isAlly: false),
            ],
        };

        var snapshots = new Dictionary<string, PlayerSnapshot>
        {
            ["Asia:1001"] = MakeSnapshot("1001", 0.60),
            ["Asia:1002"] = MakeSnapshot("1002", 0.58),
            ["Asia:2001"] = MakeSnapshot("2001", 0.45),
            ["Asia:2002"] = MakeSnapshot("2002", 0.47),
        };

        var enriched = SnapshotFeatureBuilder.Enrich(
            battle,
            (id, server, _) => snapshots.GetValueOrDefault($"{server}:{id}"));

        var ally = TeamAggregator.Aggregate(enriched.Allies);
        var enemy = TeamAggregator.Aggregate(enriched.Enemies);

        Assert.Equal(0.59, ally.AvgAccountWinrate, precision: 6);
        Assert.Equal(0.46, enemy.AvgAccountWinrate, precision: 6);
        Assert.True(ally.AvgAccountWinrate > enemy.AvgAccountWinrate);
    }
}
