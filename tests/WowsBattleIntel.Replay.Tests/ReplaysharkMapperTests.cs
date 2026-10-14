using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Replay.Dtos;

namespace WowsBattleIntel.Replay.Tests;

public class ReplaysharkMapperTests
{
    private static ReplaysharkOutput MakeOutput(
        string gameType = "RandomBattle",
        string resultType = "Win",
        double duration = 900)
    {
        return new ReplaysharkOutput
        {
            Metadata = new ReplaysharkMetadata
            {
                Map = "spaces/50_Gold_harbor",
                GameMode = "domination_4point",
                GameType = gameType,
                MatchGroup = "pvp",
                Version = new ReplaysharkVersion { Major = 15, Minor = 6, Patch = 0, Build = 12830008 },
                PlayedDuration = duration,
                Timestamp = "2026-07-22T05:29:02Z",
                BattleResult = new ReplaysharkBattleResult { Type = resultType, TeamId = 0 },
            },
            Players =
            [
                MakePlayer(1001, "AllyOne", "ASIA", 0, isSelf: true, shipClass: "Battleship", tier: 10),
                MakePlayer(1002, "AllyTwo", "ASIA", 0, shipClass: "Cruiser", tier: 8),
                MakePlayer(2001, "EnemyOne", "ASIA", 1, shipClass: "Destroyer", tier: 6),
                MakePlayer(2002, "EnemyTwo", "ASIA", 1, shipClass: "Submarine", tier: 8),
            ],
        };
    }

    private static ReplaysharkPlayer MakePlayer(
        long id,
        string name,
        string realm,
        int teamId,
        bool isSelf = false,
        string shipClass = "Cruiser",
        int tier = 8,
        long? divisionId = null)
    {
        return new ReplaysharkPlayer
        {
            DbId = id,
            Name = name,
            DisplayName = name,
            Clan = "TEST",
            Realm = realm,
            TeamId = teamId,
            Relation = teamId == 0 ? 1 : 2,
            IsSelf = isSelf,
            IsBot = false,
            IsHiddenProfile = false,
            ShipIndex = "TEST001",
            ShipId = 1234,
            ShipName = "TestShip",
            ShipClass = shipClass,
            ShipTier = tier,
            DivisionId = divisionId,
            TimeLivedSecs = 600,
            Survived = true,
            ServerResults = new ReplaysharkServerResults
            {
                Damage = 50000,
                Kills = 1,
                SpottingDamage = 10000,
                PotentialDamage = 100000,
                Xp = 1500,
            },
        };
    }

    [Fact]
    public void Map_ProducesBattleWithCorrectMetadata()
    {
        var output = MakeOutput();

        var battle = ReplaysharkMapper.Map(output);

        Assert.NotNull(battle);
        Assert.Equal("spaces/50_Gold_harbor", battle.MapId);
        Assert.Equal(BattleType.Random, battle.BattleType);
        Assert.Equal(BattleOutcome.Win, battle.Outcome);
        Assert.Equal("15.6.0", battle.GameVersion);
        Assert.Equal(900, battle.DurationSeconds);
        Assert.Equal(GameServer.Asia, battle.Server);
    }

    [Fact]
    public void Map_ZeroDuration_StillAccepts_WhenResultPresent()
    {
        // 时长缺失不应导致丢弃，部分游戏版本的 played_duration 为 null。
        var output = MakeOutput(duration: 0);

        var battle = ReplaysharkMapper.Map(output);

        Assert.NotNull(battle);
        Assert.Equal(0, battle.DurationSeconds);
    }

    [Fact]
    public void Map_MissingBothResultFields_ReturnsNull()
    {
        var output = MakeOutput();
        output.Metadata.BattleResult = null;
        output.Metadata.ResultsBattleResult = null;

        var battle = ReplaysharkMapper.Map(output);

        Assert.Null(battle);
    }

    [Fact]
    public void Map_FallsBackToResultsBattleResult()
    {
        // 15.7 起 battle_result 可能为 null，结果落在 results_battle_result。
        var output = MakeOutput();
        output.Metadata.BattleResult = null;
        output.Metadata.ResultsBattleResult = new ReplaysharkBattleResult
        {
            Type = "Win",
            TeamId = 0,
        };

        var battle = ReplaysharkMapper.Map(output);

        Assert.NotNull(battle);
        Assert.Equal(BattleOutcome.Win, battle.Outcome);
    }

    [Fact]
    public void Map_DeterminesAllyFromRecorderTeam()
    {
        var output = MakeOutput();

        var battle = ReplaysharkMapper.Map(output)!;

        Assert.Equal(2, battle.Allies.Count());
        Assert.Equal(2, battle.Enemies.Count());
        Assert.Contains(battle.Allies, p => p.PlayerName == "AllyOne");
        Assert.Contains(battle.Enemies, p => p.PlayerName == "EnemyOne");
    }

    [Fact]
    public void Map_OnLoss_UsesRecorderTeamNotWinnerTeam()
    {
        // 回归测试：battle_result.team_id 是获胜方队伍，不是录制者队伍。
        // 失败局里胜方是对手，若用 team_id 判友敌会整体判反。
        // 本用例构造录制者在 team 1 且落败（胜方 team 0）的情形。
        var output = MakeOutput(resultType: "Loss");
        output.Metadata.BattleResult!.TeamId = 0; // 胜方队伍
        // 录制者 AllyOne 与队友 AllyTwo 在 team 1，对手在 team 0。
        output.Players[0].TeamId = 1; // isSelf
        output.Players[1].TeamId = 1;
        output.Players[2].TeamId = 0;
        output.Players[3].TeamId = 0;

        var battle = ReplaysharkMapper.Map(output)!;

        Assert.Equal(BattleOutcome.Loss, battle.Outcome);
        // 友方必须是录制者所在队伍，即 team 1 的两人。
        Assert.Equal(2, battle.Allies.Count());
        Assert.Contains(battle.Allies, p => p.PlayerName == "AllyOne");
        Assert.Contains(battle.Allies, p => p.PlayerName == "AllyTwo");
        Assert.Contains(battle.Enemies, p => p.PlayerName == "EnemyOne");
        Assert.Contains(battle.Enemies, p => p.PlayerName == "EnemyTwo");
    }

    [Fact]
    public void Map_OnWin_AllyMatchesRecorderTeam()
    {
        // 胜局里胜方即录制者所在队伍，友方应为录制者队伍。
        var output = MakeOutput(resultType: "Win");
        output.Metadata.BattleResult!.TeamId = 1; // 胜方队伍与录制者一致
        output.Players[0].TeamId = 1; // isSelf
        output.Players[1].TeamId = 1;
        output.Players[2].TeamId = 0;
        output.Players[3].TeamId = 0;

        var battle = ReplaysharkMapper.Map(output)!;

        Assert.Equal(BattleOutcome.Win, battle.Outcome);
        Assert.Equal(2, battle.Allies.Count());
        Assert.Contains(battle.Allies, p => p.PlayerName == "AllyOne");
        Assert.Contains(battle.Enemies, p => p.PlayerName == "EnemyOne");
    }

    [Fact]
    public void Map_MapsShipClassCorrectly()
    {
        var output = MakeOutput();

        var battle = ReplaysharkMapper.Map(output)!;

        Assert.Equal(ShipClass.Battleship, battle.Allies.First(p => p.PlayerName == "AllyOne").ShipClass);
        Assert.Equal(ShipClass.Cruiser, battle.Allies.First(p => p.PlayerName == "AllyTwo").ShipClass);
        Assert.Equal(ShipClass.Destroyer, battle.Enemies.First(p => p.PlayerName == "EnemyOne").ShipClass);
        Assert.Equal(ShipClass.Submarine, battle.Enemies.First(p => p.PlayerName == "EnemyTwo").ShipClass);
    }

    [Fact]
    public void Map_FormatsClanTagWithBrackets()
    {
        var output = MakeOutput();

        var battle = ReplaysharkMapper.Map(output)!;

        Assert.All(battle.Players, p => Assert.Equal("[TEST]", p.ClanTag));
    }

    [Fact]
    public void Map_GeneratesStableBattleId()
    {
        var output1 = MakeOutput();
        var output2 = MakeOutput();

        var battle1 = ReplaysharkMapper.Map(output1)!;
        var battle2 = ReplaysharkMapper.Map(output2)!;

        Assert.Equal(battle1.BattleId, battle2.BattleId);
        Assert.False(string.IsNullOrWhiteSpace(battle1.BattleId));
    }

    [Fact]
    public void Map_SingleDivision_DefaultsToOne()
    {
        var output = MakeOutput();

        var battle = ReplaysharkMapper.Map(output)!;

        Assert.All(battle.Players, p => Assert.Equal(1, p.DivisionSize));
    }

    [Fact]
    public void Map_DivisionMembers_GetSharedSize()
    {
        var output = MakeOutput();
        // 三名玩家同一组队。
        output.Players[0].DivisionId = 777;
        output.Players[1].DivisionId = 777;
        output.Players[2].DivisionId = 777;

        var battle = ReplaysharkMapper.Map(output)!;

        Assert.Equal(3, battle.Players.First(p => p.PlayerName == "AllyOne").DivisionSize);
        Assert.Equal(3, battle.Players.First(p => p.PlayerName == "EnemyOne").DivisionSize);
        Assert.Equal(1, battle.Players.First(p => p.PlayerName == "EnemyTwo").DivisionSize);
    }

    [Theory]
    [InlineData("RandomBattle", BattleType.Random)]
    [InlineData("RankedBattle", BattleType.Ranked)]
    [InlineData("CoopBattle", BattleType.Coop)]
    [InlineData("CooperativeBattle", BattleType.Coop)]
    [InlineData("ClanBattle", BattleType.Clan)]
    [InlineData("UnknownType", BattleType.Unknown)]
    public void MapBattleType_MapsKnownTypes(string gameType, BattleType expected)
    {
        var actual = ReplaysharkMapper.MapBattleType(gameType, "pvp");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("brawl")]
    [InlineData("event")]
    public void MapBattleType_UnrecognizedGameTypeWithSpecialGroup_FallsBackToUnknown(
        string matchGroup)
    {
        // Brawl 与 Event 等特殊模式没有对应枚举，归 Unknown 交给上游过滤，
        // 不应被误判成 Random 之类的主流模式。
        var actual = ReplaysharkMapper.MapBattleType("BrawlBattle", matchGroup);
        Assert.Equal(BattleType.Unknown, actual);
    }

    [Fact]
    public void MapBattleType_UnknownGameType_UsesMatchGroupFallback()
    {
        // game_type 缺失时靠 match_group 兜底。
        var actual = ReplaysharkMapper.MapBattleType(string.Empty, "cooperative");
        Assert.Equal(BattleType.Coop, actual);
    }

    [Theory]
    [InlineData("RU", GameServer.Ru)]
    [InlineData("EU", GameServer.Eu)]
    [InlineData("NA", GameServer.Na)]
    [InlineData("ASIA", GameServer.Asia)]
    [InlineData("CN", GameServer.Cn)]
    [InlineData("UNKNOWN", GameServer.Auto)]
    public void MapServer_MapsKnownRealms(string realm, GameServer expected)
    {
        var actual = ReplaysharkMapper.MapServer(realm);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Win", BattleOutcome.Win)]
    [InlineData("Loss", BattleOutcome.Loss)]
    [InlineData("Draw", BattleOutcome.Draw)]
    [InlineData("", BattleOutcome.Unknown)]
    public void MapOutcome_MapsKnownResults(string resultType, BattleOutcome expected)
    {
        var actual = ReplaysharkMapper.MapOutcome(resultType);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Map_PreservesAccountId()
    {
        var output = MakeOutput();

        var battle = ReplaysharkMapper.Map(output)!;

        Assert.Contains(battle.Players, p => p.AccountId == "1001");
        Assert.Contains(battle.Players, p => p.AccountId == "2001");
    }
}
