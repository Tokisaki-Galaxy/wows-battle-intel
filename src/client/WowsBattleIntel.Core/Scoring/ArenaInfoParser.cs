using System.Globalization;
using Newtonsoft.Json;
using WowsBattleIntel.Core.Identity;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Scoring;

/// <summary>
/// tempArenaInfo.json 的读取、解析与赛前映射。
/// </summary>
/// <remarks>
/// tempArenaInfo.json 是游戏在对局加载界面写出的赛前名单，用于在回放产生之前
/// 给出预测。文件有两种形态：明文 JSON，首字节为 0x7B；或带头部的打包格式，
/// 首字节为 0x12，头部剥离逻辑逐行复刻 ApeRadar 的 ArenaInfoReader.ReadTempArenaInfoFile
/// （只读参考，位于 ApeRadar_src\ApeRadar\Utils\ArenaInfoReader.cs 第 52-95 行）。
/// 文件可能正被游戏写入，读取必须共享写与删除权限。
/// </remarks>
public static class ArenaInfoParser
{
    /// <summary>打包格式的魔数，位于文件头 4 字节。</summary>
    private static readonly byte[] PackedMagic = [0x12, 0x32, 0x34, 0x11];

    /// <summary>
    /// 船目录元数据查询，供缺失的舰种与等级回填。
    /// </summary>
    /// <remarks>
    /// Core 不引用 Stats，由 Client 启动时注入委托。未注入时缺失字段
    /// 保持原值（15.9+ 的 tempArenaInfo 为空/0）。
    /// </remarks>
    public static Func<string, (string Type, int Tier)?>? ShipMetaLookup { get; set; }

    /// <summary>
    /// 读取并解析 tempArenaInfo.json。
    /// </summary>
    /// <param name="filePath">tempArenaInfo.json 的完整路径。</param>
    /// <param name="arenaInfo">解析成功时的结果，失败时为 null。</param>
    /// <returns>解析成功返回 true。文件不存在、正在写入读到半截、格式不对或
    /// JSON 损坏一律返回 false，不抛异常，调用方直接重试即可。</returns>
    public static bool TryRead(string filePath, out ArenaInfo? arenaInfo)
    {
        arenaInfo = null;
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            string json = ReadJsonBody(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            var info = JsonConvert.DeserializeObject<ArenaInfo>(json);
            if (info is null)
            {
                return false;
            }

            // dateTime 是 "dd.MM.yyyy HH:mm:ss" 格式的本地时间，反序列化拿不到
            // DateTimeOffset，需要在这里显式解析。解析失败视为整份文件无效。
            if (!TryParseStartedAt(info.RawStartedAt, out var startedAt))
            {
                return false;
            }

            info.StartedAt = startedAt;
            arenaInfo = info;
            return true;
        }
        catch (Exception)
        {
            // 赛前链路的核心诉求是不打扰：任何解析失败都等下一轮轮询重试。
            arenaInfo = null;
            return false;
        }
    }

    /// <summary>
    /// 把赛前名单映射成战斗记录，供预测链路直接消费。
    /// </summary>
    /// <remarks>
    /// 这是赛前快照：没有结果字段，MapId 缺失，战绩未知。赛后回放解析出的
    /// 正式记录与它通过 BattleId 前后呼应，两者 BattleId 的关系见
    /// <see cref="ToBattleId"/> 的注释。
    /// </remarks>
    /// <param name="arenaInfo">解析出的赛前名单。</param>
    /// <returns>战斗记录。</returns>
    public static BattleRecord ToBattleRecord(ArenaInfo arenaInfo)
    {
        ArgumentNullException.ThrowIfNull(arenaInfo);

        var players = new List<PlayerRecord>(arenaInfo.Vehicles.Count);
        foreach (var vehicle in arenaInfo.Vehicles)
        {
            players.Add(MapPlayer(vehicle));
        }

        var battle = new BattleRecord
        {
            StartedAt = arenaInfo.StartedAt,
            Server = MapServer(arenaInfo.Realm),
            Outcome = BattleOutcome.Unknown,
            Players = players,
        };

        // gameType 比 matchGroup 更具体（RandomBattle 对 pvp），非空时优先。
        // 映射键与 ReplaysharkMapper.MapBattleType 保持同一套（Core 不引用
        // Replay，就地维护，双侧同步改）。
        battle.BattleType = string.IsNullOrEmpty(arenaInfo.GameType)
            ? BattleTypeExtensions.FromMatchGroup(arenaInfo.MatchGroup)
            : arenaInfo.GameType.Trim().ToUpperInvariant() switch
            {
                "RANDOMBATTLE" => BattleType.Random,
                "RANKEDBATTLE" => BattleType.Ranked,
                "COOPERATIVEBATTLE" or "COOPBATTLE" or "PVEBATTLE" => BattleType.Coop,
                "CLANBATTLE" => BattleType.Clan,
                "TRAININGBATTLE" or "PRIVATEBATTLE" => BattleType.Training,
                _ => BattleTypeExtensions.FromMatchGroup(arenaInfo.MatchGroup),
            };

        battle.BattleId = ToBattleId(battle);
        return battle;
    }

    /// <summary>
    /// 生成赛前对局标识。
    /// </summary>
    /// <remarks>
    /// BattleId 由开始时间、地图、类型与参战者集合哈希得出。tempArenaInfo 不含
    /// 地图字段，MapId 只能取空串，算出的哈希必然与赛后回放（MapId 真实填写）
    /// 不同。为避免同名对局在赛前链路与回放链路被误当成同一条记录，在哈希后
    /// 追加 ":pre" 后缀：同一局的时间戳与参战者一致，去掉后缀部分结构相同，
    /// 赛后可用这一性质做关联；同时前缀不等保证两条链路的记录不撞 ID。
    /// </remarks>
    private static string ToBattleId(BattleRecord battle)
    {
        // tempArenaInfo 无地图字段，MapId 传空串，由 BattleIdGenerator 正常哈希。
        string baseId = BattleIdGenerator.Generate(battle.StartedAt, string.Empty, battle.BattleType, battle.Players);
        return $"{baseId}:pre";
    }

    /// <summary>
    /// 解析 dateTime 字段。ApeRadar 用 ParseExact 配当前区域文化，这里
    /// 固定不变式文化以免区域差异导致日期月份互换。
    /// </summary>
    private static bool TryParseStartedAt(string? raw, out DateTimeOffset startedAt)
    {
        if (!string.IsNullOrWhiteSpace(raw) &&
            DateTimeOffset.TryParseExact(
                raw,
                "dd.MM.yyyy HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out startedAt))
        {
            return true;
        }

        startedAt = default;
        return false;
    }

    /// <summary>
    /// 映射单个参战者。
    /// </summary>
    private static PlayerRecord MapPlayer(ArenaVehicle vehicle)
    {
        // 15.9 起 tempArenaInfo 的 vehicles 不带 shipType/shipTier，从
        // 船目录回填。Core 不引用 Stats，通过静态委托注入避免反向依赖。
        string shipType = vehicle.ShipType;
        int shipTier = vehicle.ShipTier;
        if (ShipMetaLookup?.Invoke(vehicle.ShipId) is { } meta)
        {
            if (shipType.Length == 0)
            {
                shipType = meta.Type;
            }

            if (shipTier <= 0)
            {
                shipTier = meta.Tier;
            }
        }

        return new PlayerRecord
        {
            AccountId = vehicle.Id,
            PlayerName = vehicle.Name,
            // relation 0/1 为友方（含本方分队），2/3 为敌方。
            IsAlly = vehicle.Relation <= 1,
            // id 30 及以下是人机； ":" 前缀的昵称同样按人机处理，
            // 这是 ApeRadar 沿用的游戏侧约定，两条路都要认。
            IsBot = IsBotId(vehicle.Id) || vehicle.Name.StartsWith(":", StringComparison.Ordinal),
            ShipId = vehicle.ShipId,
            // 临时名单阶段查不到中文船名，留空由 UI 层的 ShipCatalog 兜底。
            ShipName = string.Empty,
            ShipTier = shipTier,
            ShipClass = ShipClassExtensions.Parse(shipType),
            DivisionSize = 1,

            // 赛前没有任何战绩与表现数据，全部走哨兵与零值默认。
            // PlayerRecord 的统计字段默认即 -1 哨兵，战后表现字段默认即 0，不显式赋值。
        };
    }

    /// <summary>账号 ID 是否落在人机号段。非数字 ID 按人类处理。</summary>
    private static bool IsBotId(string id) =>
        int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value <= 30;

    /// <summary>
    /// 读取文件内容并剥离头部，返回纯 JSON 文本。
    /// </summary>
    private static string ReadJsonBody(string filePath)
    {
        // 游戏正在写入时也允许读，独占打开会直接抛 IOException 导致整轮失败。
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader sr = new(fs);

        int firstByte = sr.Peek();

        // 明文 JSON，直接读完。
        if (firstByte == 0x7B)
        {
            return sr.ReadToEnd();
        }

        // 打包格式。布局（与 ApeRadar 一致）：
        //   偏移 0-3：魔数 12 32 34 11
        //   偏移 4-7：未使用的填充
        //   偏移 8-11：小端 int32，JSON 体的字符长度
        //   偏移 12 起：JSON 体
        // ApeRadar 用 Seek(8) 后 ReadToEnd 再截取前 N 个字符，等价于从
        // 偏移 12 起取前 N 个字符，这里保持同一写法以便对照。
        if (firstByte == 0x12)
        {
            fs.Seek(0, SeekOrigin.Begin);
            byte[] buffer = new byte[4];
            fs.ReadExactly(buffer, 0, 4);
            if (!buffer.AsSpan().SequenceEqual(PackedMagic))
            {
                throw new InvalidDataException("tempArenaInfo 头部魔数不符。");
            }

            fs.Seek(8, SeekOrigin.Begin);
            fs.ReadExactly(buffer, 0, 4);
            sr.DiscardBufferedData();
            int bodyLength = BitConverter.ToInt32(buffer, 0);
            string content = sr.ReadToEnd();
            if (bodyLength < 0 || bodyLength > content.Length)
            {
                throw new InvalidDataException("tempArenaInfo 头部声明的长度超出文件实际内容，可能正在写入。");
            }

            return content[..bodyLength];
        }

        throw new InvalidDataException("tempArenaInfo 既非明文 JSON 也非已知的打包格式。");
    }

    /// <summary>
    /// 映射服务器缩写。
    /// </summary>
    /// <remarks>
    /// 与 ReplaysharkMapper.MapServer 保持同一映射表。Core 不能反向引用 Replay
    /// 项目，这里就地复制一份，改动任一侧时必须同步另一侧。
    /// </remarks>
    private static GameServer MapServer(string? realm)
    {
        if (string.IsNullOrWhiteSpace(realm))
        {
            return GameServer.Auto;
        }

        return realm.Trim().ToUpperInvariant() switch
        {
            "RU" => GameServer.Ru,
            "EU" => GameServer.Eu,
            "NA" => GameServer.Na,
            "ASIA" => GameServer.Asia,
            "CN" => GameServer.Cn,
            _ => GameServer.Auto,
        };
    }
}
