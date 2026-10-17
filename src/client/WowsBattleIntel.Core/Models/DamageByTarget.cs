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
    /// <summary>目标船 ID，即对局内的数字船 ID。</summary>
    public string TargetShipId { get; set; } = string.Empty;

    /// <summary>目标船名。解析时从同一局的参战者名单反查，查不到为空串。</summary>
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
