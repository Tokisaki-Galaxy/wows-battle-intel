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
            ShipName = "TestShip",            ShipClass = shipClass,
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
    public void Map_MidQuitBattleWithDistortedTeamId_FallsBackToRelation()
    {
        // 中途死亡退出、游戏未走完结算的对局：replayshark 还原不出队伍编号，
        // team_id 被 ship_id 一类原始值填充（4285445840 量级），relation 不受
        // 影响。友敌判定必须整体退回 relation 口径，且不得被大数值破坏。
        var output = new ReplaysharkOutput
        {
            Metadata = new ReplaysharkMetadata
            {
                Map = "spaces/51_Greece",
                GameType = "EventBattle",
                MatchGroup = "event",
                Version = new ReplaysharkVersion { Major = 15, Minor = 9, Patch = 0, Build = 13357625 },
                PlayedDuration = 462.8f,
                Timestamp = "2026-10-18T04:34:57Z",
                BattleResult = new ReplaysharkBattleResult { Type = "Win", TeamId = 0 },
            },
            Players =
            [
                MakePlayer(537931438, "Tokisaki_Galaxy", "ASIA", teamId: 537931438, isSelf: true),
                MakePlayer(672061883, "kazusaRin", "ASIA", teamId: 672061883),
                MakePlayer(269936880, ":Yumashev:", "ASIA", teamId: 4285445840),
                MakePlayer(269936879, ":Warrender:", "ASIA", teamId: 4276008720),
            ],
        };

        // relation 按 MakePlayer 的默认规则由 teamId 奇偶推出，这里手动改写
        // 成中途退出对局的真实取值：真人 0/1 友方，机器人 2 敌方。
        output.Players[0].Relation = 0;
        output.Players[1].Relation = 1;
        output.Players[2].Relation = 2;
        output.Players[2].IsBot = true;
        output.Players[3].Relation = 2;
        output.Players[3].IsBot = true;

        var battle = ReplaysharkMapper.Map(output);

        Assert.NotNull(battle);
        Assert.Equal(2, battle.Allies.Count());
        Assert.Equal(2, battle.Enemies.Count());
        Assert.All(battle.Allies, p => Assert.True(p.IsAlly));
        Assert.All(battle.Enemies, p => Assert.False(p.IsAlly));
        Assert.Equal("Tokisaki_Galaxy", battle.Allies.First().PlayerName);
    }

    [Fact]
    public void Map_ShipIdUsesNumericIdNotShipIndex()
    {
        // 回归测试：ship_id 必须存数字 ID，WG API 的 ships/stats 只认它。
        // 早年误存了 ship_index 索引码，导致单船统计查询全部落空。
        var battle = ReplaysharkMapper.Map(MakeOutput())!;

        Assert.All(battle.Players, p => Assert.Equal("1234", p.ShipId));
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

    [Fact]
    public void Map_DivisionId_PreservedAndDistinguishesTwoDuos()
    {
        var output = MakeOutput();
        // 两个互不相干的双人队。人数相同，必须靠 division_id 区分。
        output.Players[0].DivisionId = 111;
        output.Players[1].DivisionId = 111;
        output.Players[2].DivisionId = 222;
        output.Players[3].DivisionId = 222;

        var battle = ReplaysharkMapper.Map(output)!;

        string? id0 = battle.Players[0].DivisionId;
        string? id1 = battle.Players[1].DivisionId;
        string? id2 = battle.Players[2].DivisionId;
        string? id3 = battle.Players[3].DivisionId;

        Assert.Equal("111", id0);
        Assert.Equal("111", id1);
        Assert.Equal("222", id2);
        Assert.Equal("222", id3);

        // 人数一样，但小队不同。
        Assert.Equal(battle.Players[0].DivisionSize, battle.Players[2].DivisionSize);
        Assert.NotEqual(id0, id2);
    }

    [Fact]
    public void Map_SoloPlayer_DivisionIdIsNull()
    {
        var output = MakeOutput();

        var battle = ReplaysharkMapper.Map(output)!;

        Assert.All(battle.Players, p => Assert.Null(p.DivisionId));
    }

    [Theory]
    [InlineData("RandomBattle", BattleType.Random)]
    [InlineData("RankedBattle", BattleType.Ranked)]
    [InlineData("CoopBattle", BattleType.Coop)]
    [InlineData("CooperativeBattle", BattleType.Coop)]
    [InlineData("ClanBattle", BattleType.Clan)]
    // matchGroup "pvp" 是 tempArenaInfo 实测值（16.10 真实文件），经 FromMatchGroup
    // 映射为 Random；gameType 未知时以 matchGroup 兜底，pvp 即随机战。
    [InlineData("UnknownType", BattleType.Random)]
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

    [Fact]
    public void Map_DamageByTarget_TranslatesTargetShipInfo()
    {
        var output = MakeOutput();
        // 录制者打了 EnemyOne 与 EnemyTwo，各自的船名等级舰种从同局名单反查。
        output.Players[2].ShipName = "Shimakaze";
        output.Players[3].ShipName = "U-2501";
        output.Players[0].ServerResults!.DamageInteractions = new Dictionary<string, ReplaysharkDamageInteraction>
        {
            ["2001"] = new() { DamageDealt = 30000, DamageReceived = 4000 },
            ["2002"] = new() { DamageDealt = 12000, DamageReceived = 9000 },
        };

        var battle = ReplaysharkMapper.Map(output)!;

        var recorder = battle.Players.First(p => p.AccountId == "1001");
        Assert.Equal(2, recorder.DamageByTarget.Count);

        var shimakaze = recorder.DamageByTarget.First(d => d.TargetPlayerId == "2001");
        Assert.Equal("Shimakaze", shimakaze.TargetShipName);
        Assert.Equal(6, shimakaze.TargetShipTier);
        Assert.Equal(ShipClass.Destroyer, shimakaze.TargetShipClass);
        Assert.Equal(30000, shimakaze.DamageDealt);
        Assert.Equal(4000, shimakaze.DamageReceived);
    }

    [Fact]
    public void Map_DamageByTarget_FiltersZeroBothWays()
    {
        var output = MakeOutput();
        output.Players[0].ServerResults!.DamageInteractions = new Dictionary<string, ReplaysharkDamageInteraction>
        {
            ["2001"] = new() { DamageDealt = 0, DamageReceived = 0 },
            ["2002"] = new() { DamageDealt = 500, DamageReceived = 0 },
            ["1002"] = new() { DamageDealt = 0, DamageReceived = 700 },
        };

        var battle = ReplaysharkMapper.Map(output)!;

        var recorder = battle.Players.First(p => p.AccountId == "1001");
        // 双方都是 0 的条目没有信息量，只留有一条非零的。
        Assert.Equal(2, recorder.DamageByTarget.Count);
        Assert.DoesNotContain(recorder.DamageByTarget, d => d.TargetPlayerId == "2001");
    }

    [Fact]
    public void Map_DamageByTarget_SortsByDealtDamageDescending()
    {
        var output = MakeOutput();
        output.Players[1].ShipName = "Des Moines";
        output.Players[2].ShipName = "Shimakaze";
        output.Players[3].ShipName = "U-2501";
        output.Players[0].ServerResults!.DamageInteractions = new Dictionary<string, ReplaysharkDamageInteraction>
        {
            ["2001"] = new() { DamageDealt = 1000 },
            ["2002"] = new() { DamageDealt = 50000 },
            // 同伤害值，靠船名定序：Des Moines 排在 Shimakaze 之前。
            ["1002"] = new() { DamageDealt = 20000 },
            ["2003"] = new() { DamageDealt = 20000 },
        };
        output.Players.Add(MakePlayer(2003, "EnemyThree", "ASIA", 1, shipClass: "Cruiser", tier: 10));
        output.Players[4].ShipName = "Shimakaze";

        var battle = ReplaysharkMapper.Map(output)!;

        var recorder = battle.Players.First(p => p.AccountId == "1001");
        Assert.Equal(["2002", "1002", "2003", "2001"], recorder.DamageByTarget.Select(d => d.TargetPlayerId));
        Assert.Equal(50000, recorder.DamageByTarget[0].DamageDealt);
    }

    [Fact]
    public void Map_DamageByTarget_UnknownTarget_KeepsDamageWithBlankShip()
    {
        // 名单里查不到的目标仍然保留，只是船信息落空，伤害本身可信。
        var output = MakeOutput();
        output.Players[0].ServerResults!.DamageInteractions = new Dictionary<string, ReplaysharkDamageInteraction>
        {
            ["7777"] = new() { DamageDealt = 1234, DamageReceived = 0 },
        };

        var battle = ReplaysharkMapper.Map(output)!;

        var recorder = battle.Players.First(p => p.AccountId == "1001");
        var entry = Assert.Single(recorder.DamageByTarget);
        Assert.Equal("7777", entry.TargetPlayerId);
        Assert.Equal(string.Empty, entry.TargetShipId);
        Assert.Equal(string.Empty, entry.TargetShipName);
        Assert.Equal(ShipClass.Unknown, entry.TargetShipClass);
    }

    [Fact]
    public void Map_NoDamageInteractions_YieldsEmptyList()
    {
        var battle = ReplaysharkMapper.Map(MakeOutput())!;

        Assert.All(battle.Players, p => Assert.Empty(p.DamageByTarget));
    }

    [Fact]
    public void Map_Achievements_FormatsCountAboveOne()
    {
        var output = MakeOutput();
        output.Players[0].Achievements =
        [
            new ReplaysharkAchievement { Name = "unsinkable", DisplayName = "不死鸟", Count = 1 },
            new ReplaysharkAchievement { Name = "kill", DisplayName = "击毁", Count = 3 },
        ];

        var battle = ReplaysharkMapper.Map(output)!;

        var recorder = battle.Players.First(p => p.AccountId == "1001");
        Assert.Equal(["不死鸟", "击毁 ×3"], recorder.Achievements);
        // 没有成就的玩家保持空列表。
        Assert.Empty(battle.Players.First(p => p.AccountId == "2001").Achievements);
    }

    [Fact]
    public void Map_AchievementWithoutDisplayName_FallsBackToName()
    {
        var output = MakeOutput();
        output.Players[3].Achievements =
        [
            new ReplaysharkAchievement { Name = "detonation", DisplayName = string.Empty, Count = 1 },
        ];

        var battle = ReplaysharkMapper.Map(output)!;

        var enemy = battle.Players.First(p => p.AccountId == "2002");

        // display_name 缺失时退回内部名；内部名同样是英文，翻译层照常生效。
        // 成就表已切到亚服口径，Detonation 在 zh_sg 里是「爆破手」。
        Assert.Equal(["爆破手"], enemy.Achievements);
    }
}
