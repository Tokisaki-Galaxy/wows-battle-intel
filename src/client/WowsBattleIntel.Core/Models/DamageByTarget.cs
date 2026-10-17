namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 本局某名玩家对某一艘目标船的伤害往来明细。
/// </summary>
/// <remarks>
/// 单局总伤害只能说明打了多少，说不清打给了谁。切回放复盘时真正要看的是
/// 这局把伤害集中在哪艘船、又被哪艘船压着打，这套明细是 replayshark 的
/// damage_interactions 的唯一留存形式，重解析前只有它可用。
/// </remarks>
public sealed class DamageByTarget
{
    /// <summary>目标玩家的账号 ID。</summary>
    /// <remarks>
    /// 来自回放 damage_interactions 字典的键，是对方玩家的 db_id，
    /// 不是船 ID。界面不展示它，只用于去重与排查。
    /// </remarks>
    public string TargetPlayerId { get; set; } = string.Empty;

    /// <summary>
    /// 目标船的数字船 ID，来自同一局名单反查。
    /// </summary>
    /// <remarks>
    /// 依赖它才能走船目录查到中文名。早期入库的记录没有这一列，
    /// 展示层会退回按英文船名反查。
    /// </remarks>
    public string TargetShipId { get; set; } = string.Empty;

    /// <summary>
    /// 目标船名，回放原始给的是英文名。
    /// </summary>
    /// <remarks>
    /// 展示时统一经船目录翻成中文；这里保留原文，避免丢失可追溯信息。
    /// </remarks>
    public string TargetShipName { get; set; } = string.Empty;

    /// <summary>目标船等级，0 表示未知。</summary>
    public int TargetShipTier { get; set; }

    /// <summary>目标舰种。</summary>
    public ShipClass TargetShipClass { get; set; } = ShipClass.Unknown;

    /// <summary>我对该目标造成的伤害。</summary>
    public long DamageDealt { get; set; }

    /// <summary>该目标对我造成的伤害。</summary>
    public long DamageReceived { get; set; }
}
