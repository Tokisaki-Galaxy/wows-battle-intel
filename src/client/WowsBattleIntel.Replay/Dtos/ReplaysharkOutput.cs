using Newtonsoft.Json;

namespace WowsBattleIntel.Replay.Dtos;

/// <summary>
/// replayshark battle-results --format normalized 输出的顶层结构。
/// </summary>
/// <remarks>
/// 字段命名严格对应 replayshark 的 JSON 输出，用 JsonProperty 显式绑定，
/// 避免依赖属性名推断，便于应对上游字段变化。
/// </remarks>
public sealed class ReplaysharkOutput
{
    /// <summary>战斗元数据。</summary>
    [JsonProperty("metadata")]
    public ReplaysharkMetadata Metadata { get; set; } = new();

    /// <summary>参战者列表。</summary>
    [JsonProperty("players")]
    public List<ReplaysharkPlayer> Players { get; set; } = [];
}

/// <summary>
/// 战斗元数据。
/// </summary>
public sealed class ReplaysharkMetadata
{
    /// <summary>地图路径，如 spaces/50_Gold_harbor。</summary>
    [JsonProperty("map")]
    public string Map { get; set; } = string.Empty;

    /// <summary>模式，如 domination_4point。</summary>
    [JsonProperty("game_mode")]
    public string GameMode { get; set; } = string.Empty;

    /// <summary>对局类型，如 RandomBattle。</summary>
    [JsonProperty("game_type")]
    public string GameType { get; set; } = string.Empty;

    /// <summary>分组，如 pvp。</summary>
    [JsonProperty("match_group")]
    public string MatchGroup { get; set; } = string.Empty;

    /// <summary>游戏版本。</summary>
    [JsonProperty("version")]
    public ReplaysharkVersion Version { get; set; } = new();

    /// <summary>最大时长秒。</summary>
    [JsonProperty("max_duration")]
    public double MaxDuration { get; set; }

    /// <summary>实际时长秒。为 0 或 null 表示未完成对局。</summary>
    [JsonProperty("played_duration")]
    public double? PlayedDuration { get; set; }

    /// <summary>加时秒，可能为 null。</summary>
    [JsonProperty("extra_duration")]
    public double? ExtraDuration { get; set; }

    /// <summary>开始时间，ISO 8601 UTC。</summary>
    [JsonProperty("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>战斗结果，可能为 null。</summary>
    [JsonProperty("battle_result")]
    public ReplaysharkBattleResult? BattleResult { get; set; }

    /// <summary>结果来源，可能为 null。</summary>
    [JsonProperty("results_battle_result")]
    public ReplaysharkBattleResult? ResultsBattleResult { get; set; }
}

/// <summary>
/// 游戏版本。
/// </summary>
public sealed class ReplaysharkVersion
{
    /// <summary>主版本号。</summary>
    [JsonProperty("major")]
    public int Major { get; set; }

    /// <summary>次版本号。</summary>
    [JsonProperty("minor")]
    public int Minor { get; set; }

    /// <summary>补丁号。</summary>
    [JsonProperty("patch")]
    public int Patch { get; set; }

    /// <summary>构建号。</summary>
    [JsonProperty("build")]
    public long Build { get; set; }

    /// <summary>拼接为 major.minor.patch 形式。</summary>
    public string ToVersionString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>
/// 战斗结果。
/// </summary>
public sealed class ReplaysharkBattleResult
{
    /// <summary>结果类型，Win 或 Loss。</summary>
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>录制者所在队伍编号。</summary>
    /// <remarks>
    /// 与玩家条目的 team_id 同理，中途退出的对局会填进失真值，用 long 承接。
    /// </remarks>
    [JsonProperty("team_id")]
    public long TeamId { get; set; }
}

/// <summary>
/// 单个参战者。
/// </summary>
public sealed class ReplaysharkPlayer
{
    /// <summary>账号 ID，对应 WG API 的 account_id。</summary>
    [JsonProperty("db_id")]
    public long DbId { get; set; }

    /// <summary>昵称。</summary>
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>显示昵称。</summary>
    [JsonProperty("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>公会标签。</summary>
    [JsonProperty("clan")]
    public string Clan { get; set; } = string.Empty;

    /// <summary>服务器，如 ASIA。</summary>
    [JsonProperty("realm")]
    public string Realm { get; set; } = string.Empty;

    /// <summary>组队 ID，null 表示单排。</summary>
    [JsonProperty("division_id")]
    public long? DivisionId { get; set; }

    /// <summary>队伍编号。</summary>
    /// <remarks>
    /// 正常对局里是 0/1，但玩家中途死亡退出、游戏未走完结算的对局，
    /// replayshark 还原不出队伍编号，会填进 ship_id 一类的原始值，量级
    /// 超出 Int32。用 long 承接避免反序列化直接炸掉整份输出。
    /// </remarks>
    [JsonProperty("team_id")]
    public long TeamId { get; set; }

    /// <summary>关系，0/1 为友，2 为敌。</summary>
    [JsonProperty("relation")]
    public int Relation { get; set; }

    /// <summary>是否为录制者。</summary>
    [JsonProperty("is_self")]
    public bool IsSelf { get; set; }

    /// <summary>是否机器人。</summary>
    [JsonProperty("is_bot")]
    public bool IsBot { get; set; }

    /// <summary>是否被标记滥用。</summary>
    [JsonProperty("is_abuser")]
    public bool IsAbuser { get; set; }

    /// <summary>是否隐藏战绩。</summary>
    [JsonProperty("is_hidden_profile")]
    public bool IsHiddenProfile { get; set; }

    /// <summary>船的内部索引，如 PASS710。</summary>
    [JsonProperty("ship_index")]
    public string ShipIndex { get; set; } = string.Empty;

    /// <summary>船 ID。</summary>
    [JsonProperty("ship_id")]
    public long ShipId { get; set; }

    /// <summary>船名。</summary>
    [JsonProperty("ship_name")]
    public string ShipName { get; set; } = string.Empty;

    /// <summary>国籍。</summary>
    [JsonProperty("ship_nation")]
    public string ShipNation { get; set; } = string.Empty;

    /// <summary>舰种，如 Submarine。</summary>
    [JsonProperty("ship_class")]
    public string ShipClass { get; set; } = string.Empty;

    /// <summary>等级。</summary>
    [JsonProperty("ship_tier")]
    public int ShipTier { get; set; }

    /// <summary>是否测试船。</summary>
    [JsonProperty("is_test_ship")]
    public bool IsTestShip { get; set; }

    /// <summary>服务器权威战后统计。</summary>
    [JsonProperty("server_results")]
    public ReplaysharkServerResults? ServerResults { get; set; }

    /// <summary>客户端观测结果。</summary>
    [JsonProperty("observed_results")]
    public ReplaysharkObservedResults? ObservedResults { get; set; }

    /// <summary>存活秒数，可能为 null。</summary>
    [JsonProperty("time_lived_secs")]
    public int? TimeLivedSecs { get; set; }

    /// <summary>是否存活。</summary>
    [JsonProperty("survived")]
    public bool Survived { get; set; }

    /// <summary>个人评分，可能为 null。</summary>
    [JsonProperty("personal_rating")]
    public double? PersonalRating { get; set; }

    /// <summary>本局获得的成就。</summary>
    [JsonProperty("achievements")]
    public List<ReplaysharkAchievement> Achievements { get; set; } = [];
}

/// <summary>
/// 服务器权威战后统计。仅保留项目需要的字段。
/// </summary>
public sealed class ReplaysharkServerResults
{
    /// <summary>获得经验。</summary>
    [JsonProperty("xp")]
    public long Xp { get; set; }

    /// <summary>造成伤害。</summary>
    [JsonProperty("damage")]
    public long Damage { get; set; }

    /// <summary>命中数。</summary>
    [JsonProperty("hits")]
    public long Hits { get; set; }

    /// <summary>点亮伤害。</summary>
    [JsonProperty("spotting_damage")]
    public long SpottingDamage { get; set; }

    /// <summary>潜在伤害。</summary>
    [JsonProperty("potential_damage")]
    public long PotentialDamage { get; set; }

    /// <summary>承受伤害。</summary>
    [JsonProperty("received_damage")]
    public long ReceivedDamage { get; set; }

    /// <summary>击杀数。</summary>
    [JsonProperty("kills")]
    public long Kills { get; set; }

    /// <summary>造成起火数。</summary>
    [JsonProperty("fires_dealt")]
    public long FiresDealt { get; set; }

    /// <summary>造成进水数。</summary>
    [JsonProperty("floods_dealt")]
    public long FloodsDealt { get; set; }

    /// <summary>核心区命中数。</summary>
    [JsonProperty("citadels_dealt")]
    public long CitadelsDealt { get; set; }

    /// <summary>击穿数。</summary>
    [JsonProperty("crits_dealt")]
    public long CritsDealt { get; set; }

    /// <summary>航行距离。</summary>
    [JsonProperty("distance_traveled")]
    public double DistanceTraveled { get; set; }

    /// <summary>
    /// 与每艘目标船的伤害往来。键是目标玩家的 db_id，值是双方互相对同一艘船
    /// 的伤害口径，由 replayshark 按目标聚合后给出。
    /// </summary>
    /// <remarks>
    /// 键是 JSON 对象的属性名，必然是字符串，这里保持字符串形式不做转换。
    /// </remarks>
    [JsonProperty("damage_interactions")]
    public Dictionary<string, ReplaysharkDamageInteraction> DamageInteractions { get; set; } = [];
}

/// <summary>
/// 单个目标船维度的伤害往来。只声明用得到的两个字段，其余字段由反序列化器忽略。
/// </summary>
public sealed class ReplaysharkDamageInteraction
{
    /// <summary>我对该目标造成的伤害。</summary>
    [JsonProperty("damage_dealt")]
    public long DamageDealt { get; set; }

    /// <summary>该目标对我造成的伤害。</summary>
    [JsonProperty("damage_received")]
    public long DamageReceived { get; set; }
}

/// <summary>
/// 单条成就。
/// </summary>
public sealed class ReplaysharkAchievement
{
    /// <summary>成就的内部名称。</summary>
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>成就的展示名称，优先用它入库。</summary>
    [JsonProperty("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>图标资源键，随游戏版本变化，暂不入库。</summary>
    [JsonProperty("icon_key")]
    public string IconKey { get; set; } = string.Empty;

    /// <summary>本局该成就的计得次数。</summary>
    [JsonProperty("count")]
    public int Count { get; set; }

    /// <summary>成就说明。</summary>
    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// 客户端观测结果。
/// </summary>
public sealed class ReplaysharkObservedResults
{
    /// <summary>观测伤害。</summary>
    [JsonProperty("damage")]
    public long Damage { get; set; }

    /// <summary>观测击杀。</summary>
    [JsonProperty("kills")]
    public long Kills { get; set; }
}
