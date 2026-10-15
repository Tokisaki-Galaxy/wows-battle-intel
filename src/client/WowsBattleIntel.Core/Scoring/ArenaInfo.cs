using Newtonsoft.Json;

namespace WowsBattleIntel.Core.Scoring;

/// <summary>
/// tempArenaInfo.json 的解析结果，对局加载界面阶段的赛前名单。
/// </summary>
/// <remarks>
/// 游戏在对局加载时把全部参战者写进 GamePath\replays\tempArenaInfo.json，
/// 这是赛前链路唯一的数据来源。字段口径与游戏直接写出的一致，不做归一。
/// </remarks>
public sealed class ArenaInfo
{
    /// <summary>对局分组原始字符串，如 RANDOM、RANKED、COOP。</summary>
    public string MatchGroup { get; set; } = string.Empty;

    /// <summary>游戏写出的原始时间字符串，格式 dd.MM.yyyy HH:mm:ss，本地时区。</summary>
    /// <remarks>仅供 <see cref="ArenaInfoParser"/> 解析用，外部请读 <see cref="StartedAt"/>。</remarks>
    [JsonProperty("dateTime")]
    internal string? RawStartedAt { get; set; }

    /// <summary>对局开始时间，由 dateTime 按本地时区解析而来。</summary>
    [JsonIgnore]
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>服务器缩写，如 ASIA、CN、EU。</summary>
    public string Realm { get; set; } = string.Empty;

    /// <summary>当前用户账号 ID。</summary>
    public string PlayerId { get; set; } = string.Empty;

    /// <summary>参战者列表，通常 24 项，含人机。</summary>
    public List<ArenaVehicle> Vehicles { get; set; } = [];
}

/// <summary>
/// tempArenaInfo.json 里单个参战者的原始条目。
/// </summary>
public sealed class ArenaVehicle
{
    /// <summary>账号 ID 原始数字。30 及以下是人机。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>对局时的昵称快照。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>阵营编码。0/1 为友方，2/3 为敌方。</summary>
    public int Relation { get; set; }

    /// <summary>数字船 ID，WG API 只认它。</summary>
    public string ShipId { get; set; } = string.Empty;

    /// <summary>舰种原始字符串，如 Destroyer。</summary>
    public string ShipType { get; set; } = string.Empty;

    /// <summary>船等级，1 到 11。</summary>
    public int ShipTier { get; set; }
}
