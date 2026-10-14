namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 舰种。用于队伍聚合与战力加权。
/// </summary>
public enum ShipClass
{
    /// <summary>未知舰种。</summary>
    Unknown = 0,

    /// <summary>驱逐舰。</summary>
    Destroyer,

    /// <summary>巡洋舰。</summary>
    Cruiser,

    /// <summary>战列舰。</summary>
    Battleship,

    /// <summary>航空母舰。</summary>
    Carrier,

    /// <summary>潜艇。</summary>
    Submarine,
}

/// <summary>
/// 舰种的解析与稳定存储字符串。
/// </summary>
public static class ShipClassExtensions
{
    /// <summary>
    /// 从舰船目录里的类型字符串解析舰种。
    /// 无法识别时返回 <see cref="ShipClass.Unknown"/>。
    /// </summary>
    public static ShipClass Parse(string? shipType)
    {
        if (string.IsNullOrWhiteSpace(shipType))
        {
            return ShipClass.Unknown;
        }

        return shipType.Trim().ToUpperInvariant() switch
        {
            "DD" or "DESTROYER" => ShipClass.Destroyer,
            "CA" or "CL" or "CRUISER" => ShipClass.Cruiser,
            "BB" or "BATTLESHIP" => ShipClass.Battleship,
            "CV" or "CARRIER" or "AIR_CARRIER" => ShipClass.Carrier,
            "SS" or "SUBMARINE" => ShipClass.Submarine,
            _ => ShipClass.Unknown,
        };
    }

    /// <summary>转成稳定的存储字符串。</summary>
    public static string ToStorageString(this ShipClass shipClass) => shipClass.ToString();
}
