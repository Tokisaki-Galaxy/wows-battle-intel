using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Core.Tests.Scoring;

public class ArenaInfoParserTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string filePath;

    public ArenaInfoParserTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "wbi-arena-info-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        filePath = Path.Combine(tempDirectory, "tempArenaInfo.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论。
        }
    }

    /// <summary>
    /// 构造一份 24 项参战者的明文样例。双方各 12 人，含普通玩家、
    /// 低号段人机、冒号昵称人机与隐藏战绩玩家。
    /// </summary>
    private static string BuildSampleJson()
    {
        var vehicles = new List<string>();
        for (int i = 0; i < 24; i++)
        {
            // 前 12 位友方（relation 0/1 交替），后 12 位敌方。
            int relation = i < 12 ? i % 2 : 2 + i % 2;
            // 友方第 0 位放一个 id<=30 的人机，敌方第 12 位放一个 ":" 前缀人机，
            // 友方第 1 位放一个空昵称的隐藏战绩玩家。
            string id = i switch
            {
                0 => "20",
                1 => "1000000001",
                12 => "5",
                _ => (1000000000 + i).ToString(),
            };
            string name = i switch
            {
                0 => "Bot_Ally",
                1 => string.Empty,
                12 => ":enemy_bot",
                _ => $"player_{i}",
            };
            vehicles.Add($$"""
                {"id": {{id}}, "name": {{JsonString(name)}}, "relation": {{relation}}, "shipId": "{{4286581900 + i}}", "shipType": "{{ShipTypeOf(i)}}", "shipTier": {{TierOf(i)}}}
                """);
        }

        return $$"""
            {
              "matchGroup": "RANDOM",
              "dateTime": "22.07.2026 12:43:08",
              "vehicles": [{{string.Join(",\n    ", vehicles)}}],
              "playerId": "1000000002",
              "realm": "ASIA"
            }
            """;
    }

    private static string JsonString(string value) => Newtonsoft.Json.JsonConvert.ToString(value);

    private static string ShipTypeOf(int i) =>
        i % 4 == 0 ? "Destroyer" : i % 4 == 1 ? "Cruiser" : i % 4 == 2 ? "Battleship" : "AirCarrier";

    private static int TierOf(int i) => 5 + i % 7;

    [Fact]
    public void TryRead_PlainJson_ParsesAllFields()
    {
        File.WriteAllText(filePath, BuildSampleJson());

        bool ok = ArenaInfoParser.TryRead(filePath, out var arenaInfo);

        Assert.True(ok);
        Assert.NotNull(arenaInfo);
        Assert.Equal("RANDOM", arenaInfo!.MatchGroup);
        Assert.Equal("ASIA", arenaInfo.Realm);
        Assert.Equal("1000000002", arenaInfo.PlayerId);
        Assert.Equal(24, arenaInfo.Vehicles.Count);
        // dateTime 是 dd.MM.yyyy HH:mm:ss 本地时间。
        var expected = new DateTimeOffset(2026, 7, 22, 12, 43, 8, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 7, 22, 12, 43, 8)));
        Assert.Equal(expected, arenaInfo.StartedAt);
        // 抽查首尾条目。
        Assert.Equal("20", arenaInfo.Vehicles[0].Id);
        Assert.Equal("Bot_Ally", arenaInfo.Vehicles[0].Name);
        Assert.Equal("4286581900", arenaInfo.Vehicles[0].ShipId);
        Assert.Equal(0, arenaInfo.Vehicles[0].Relation);
        Assert.Equal("Destroyer", arenaInfo.Vehicles[0].ShipType);
        Assert.Equal(5, arenaInfo.Vehicles[0].ShipTier);
        Assert.Equal("4286581923", arenaInfo.Vehicles[23].ShipId);
        Assert.Equal(3, arenaInfo.Vehicles[23].Relation);
    }

    [Fact]
    public void TryRead_PackedHeader_StripsHeaderAndParses()
    {
        // 手工构造打包格式：魔数 4 字节 + 填充 4 字节 + 小端 int32 长度 + JSON 体。
        string json = BuildSampleJson();
        byte[] body = System.Text.Encoding.UTF8.GetBytes(json);
        using (var fs = File.Create(filePath))
        {
            fs.WriteByte(0x12);
            fs.WriteByte(0x32);
            fs.WriteByte(0x34);
            fs.WriteByte(0x11);
            fs.Write([0xAA, 0xBB, 0xCC, 0xDD]); // 填充，ApeRadar 不校验这段内容。
            fs.Write(BitConverter.GetBytes(json.Length)); // 小端 int32，字符长度。
            fs.Write(body);
        }

        bool ok = ArenaInfoParser.TryRead(filePath, out var arenaInfo);

        Assert.True(ok);
        Assert.NotNull(arenaInfo);
        Assert.Equal("RANDOM", arenaInfo!.MatchGroup);
        Assert.Equal(24, arenaInfo.Vehicles.Count);
        Assert.Equal("1000000002", arenaInfo.PlayerId);
    }

    [Fact]
    public void TryRead_PackedHeaderWrongMagic_ReturnsFalse()
    {
        // 打包格式但魔数不符，必须拒绝而不是硬解。
        byte[] content = [0x12, 0x00, 0x00, 0x00, 0, 0, 0, 0, 4, 0, 0, 0, (byte)'{', (byte)'}'];
        File.WriteAllBytes(filePath, content);

        bool ok = ArenaInfoParser.TryRead(filePath, out var arenaInfo);

        Assert.False(ok);
        Assert.Null(arenaInfo);
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsFalse()
    {
        bool ok = ArenaInfoParser.TryRead(Path.Combine(tempDirectory, "not-exist.json"), out var arenaInfo);

        Assert.False(ok);
        Assert.Null(arenaInfo);
    }

    [Fact]
    public void TryRead_CorruptedJson_ReturnsFalse()
    {
        File.WriteAllText(filePath, "{\"matchGroup\": \"RANDOM\", ");

        bool ok = ArenaInfoParser.TryRead(filePath, out var arenaInfo);

        Assert.False(ok);
        Assert.Null(arenaInfo);
    }

    [Fact]
    public void TryRead_EmptyFile_ReturnsFalse()
    {
        File.WriteAllBytes(filePath, []);

        bool ok = ArenaInfoParser.TryRead(filePath, out var arenaInfo);

        Assert.False(ok);
        Assert.Null(arenaInfo);
    }

    [Fact]
    public void TryRead_UnknownFirstByte_ReturnsFalse()
    {
        File.WriteAllBytes(filePath, [0x00, 0x01, 0x02]);

        bool ok = ArenaInfoParser.TryRead(filePath, out var arenaInfo);

        Assert.False(ok);
        Assert.Null(arenaInfo);
    }

    [Fact]
    public void ToBattleRecord_SplitsTeamsAndDetectsBots()
    {
        File.WriteAllText(filePath, BuildSampleJson());
        Assert.True(ArenaInfoParser.TryRead(filePath, out var arenaInfo));

        var battle = ArenaInfoParser.ToBattleRecord(arenaInfo!);

        // 敌我划分：relation 0/1 友方，2/3 敌方，各自 12 人。
        Assert.Equal(12, battle.Allies.Count());
        Assert.Equal(12, battle.Enemies.Count());
        Assert.All(battle.Allies, p => Assert.True(p.IsAlly));
        Assert.All(battle.Enemies, p => Assert.False(p.IsAlly));

        // 人机判定两条路：id<=30 号段与 ":" 昵称前缀。
        var allyBot = battle.Players.Single(p => p.AccountId == "20");
        Assert.True(allyBot.IsBot);
        var enemyBot = battle.Players.Single(p => p.AccountId == "5");
        Assert.True(enemyBot.IsBot);
        Assert.StartsWith(":", enemyBot.PlayerName);
        var human = battle.Players.Single(p => p.AccountId == "1000000002");
        Assert.False(human.IsBot);

        // 空昵称按隐藏战绩处理为人类玩家，不误判成人机。
        var hidden = battle.Players.Single(p => p.PlayerName.Length == 0);
        Assert.False(hidden.IsBot);
    }

    [Fact]
    public void ToBattleRecord_MapsBattleTypeAndMetadata()
    {
        File.WriteAllText(filePath, BuildSampleJson());
        Assert.True(ArenaInfoParser.TryRead(filePath, out var arenaInfo));

        var battle = ArenaInfoParser.ToBattleRecord(arenaInfo!);

        Assert.Equal(BattleType.Random, battle.BattleType);
        Assert.Equal(GameServer.Asia, battle.Server);
        Assert.Equal(BattleOutcome.Unknown, battle.Outcome);
        Assert.Equal(0, battle.AllyScore);
        Assert.Equal(0, battle.EnemyScore);
        Assert.Equal(0, battle.DurationSeconds);
        Assert.Equal(string.Empty, battle.MapId);
        Assert.Equal(new DateTimeOffset(2026, 7, 22, 12, 43, 8, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 7, 22, 12, 43, 8))), battle.StartedAt);
    }

    [Fact]
    public void ToBattleRecord_PlayerFieldsCarryPreBattleSentinels()
    {
        File.WriteAllText(filePath, BuildSampleJson());
        Assert.True(ArenaInfoParser.TryRead(filePath, out var arenaInfo));

        var battle = ArenaInfoParser.ToBattleRecord(arenaInfo!);
        var ship = battle.Players.Single(p => p.AccountId == "1000000002");

        Assert.Equal("1000000002", ship.AccountId);
        Assert.Equal("player_2", ship.PlayerName);
        Assert.Equal("4286581902", ship.ShipId);
        // i=2 落在 Battleship 槽位（i % 4 == 2）。
        Assert.Equal(ShipClass.Battleship, ship.ShipClass);
        Assert.Equal(7, ship.ShipTier);
        Assert.Equal(string.Empty, ship.ClanTag);
        Assert.Equal(1, ship.DivisionSize);
        // 战绩哨兵：-1 表示未知，战后表现全 0。
        Assert.Equal(-1, ship.AccountBattles);
        Assert.Equal(-1, ship.AccountWinrate);
        Assert.Equal(-1, ship.ShipBattles);
        Assert.Equal(-1, ship.WeightedWinrate);
        Assert.Equal(0, ship.DamageDealt);
        Assert.Equal(0, ship.Kills);
        Assert.Equal(0, ship.ExpEarned);
    }

    [Fact]
    public void ToBattleRecord_BattleIdStableAndSuffixed()
    {
        File.WriteAllText(filePath, BuildSampleJson());
        Assert.True(ArenaInfoParser.TryRead(filePath, out var arenaInfo));

        var battle1 = ArenaInfoParser.ToBattleRecord(arenaInfo!);
        var battle2 = ArenaInfoParser.ToBattleRecord(arenaInfo!);

        // 同一份名单重复映射必须得到同一 ID，且带 :pre 后缀。
        Assert.Equal(battle1.BattleId, battle2.BattleId);
        Assert.EndsWith(":pre", battle1.BattleId, StringComparison.Ordinal);

        // 参战者顺序打乱不影响 ID。
        var shuffled = new ArenaInfo
        {
            MatchGroup = arenaInfo!.MatchGroup,
            StartedAt = arenaInfo.StartedAt,
            Realm = arenaInfo.Realm,
            PlayerId = arenaInfo.PlayerId,
            Vehicles = [.. Enumerable.Reverse(arenaInfo.Vehicles)],
        };
        var battle3 = ArenaInfoParser.ToBattleRecord(shuffled);
        Assert.Equal(battle1.BattleId, battle3.BattleId);
    }

    [Fact]
    public void TryRead_PartiallyWrittenFile_ReadsOrFailsSafely()
    {
        // 模拟游戏写入到一半：完整 JSON 后面还拖着半截垃圾字节。
        // 允许两种结果：读出可用部分（Newtonsoft 容忍尾随内容）或安全返回
        // false。唯独不允许抛异常或返回非 null 的残缺对象。
        File.WriteAllText(filePath, BuildSampleJson());
        using var writer = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write([0x00, 0x01, 0x02, 0x03]);

        bool ok = ArenaInfoParser.TryRead(filePath, out var arenaInfo);

        if (ok)
        {
            // 读出可用部分时字段必须完整有效。
            Assert.NotNull(arenaInfo);
            Assert.Equal(24, arenaInfo!.Vehicles.Count);
        }
        else
        {
            Assert.Null(arenaInfo);
        }
    }

    [Fact]
    public void TryRead_FileOpenedByAnotherWriter_CanReadConcurrently()
    {
        // 另一个进程式写入者长期持有写锁，读取方仍应能打开文件。
        File.WriteAllText(filePath, BuildSampleJson());
        using var holder = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        bool ok = ArenaInfoParser.TryRead(filePath, out var arenaInfo);

        Assert.True(ok);
        Assert.NotNull(arenaInfo);
        Assert.Equal(24, arenaInfo!.Vehicles.Count);
    }

    [Fact]
    public void ToBattleRecord_MapsMatchGroupVariants()
    {
        var cases = new (string MatchGroup, BattleType Expected)[]
        {
            ("RANDOM", BattleType.Random),
            ("RANKED", BattleType.Ranked),
            ("COOP", BattleType.Coop),
            ("CLAN", BattleType.Clan),
            ("TRAINING", BattleType.Training),
            ("whatever", BattleType.Unknown),
        };

        foreach (var (matchGroup, expected) in cases)
        {
            var info = new ArenaInfo
            {
                MatchGroup = matchGroup,
                StartedAt = DateTimeOffset.UtcNow,
                Vehicles = [],
            };
            Assert.Equal(expected, ArenaInfoParser.ToBattleRecord(info).BattleType);
        }
    }
}
