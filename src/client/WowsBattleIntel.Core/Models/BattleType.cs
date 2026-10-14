namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 对局类型。取值来自回放与 tempArenaInfo 的 matchGroup 字段。
/// </summary>
public enum BattleType
{
    /// <summary>未知类型，解析失败或新类型。</summary>
    Unknown = 0,

    /// <summary>随机战。</summary>
    Random,

    /// <summary>排位战。</summary>
    Ranked,

    /// <summary>联合作战，含机器人。</summary>
    Coop,

    /// <summary>训练房。</summary>
    Training,

    /// <summary>公会战。</summary>
    Clan,

    /// <summary>剧情行动。</summary>
    Operation,
}

/// <summary>
/// 对局类型与游戏 matchGroup 字符串的映射。
/// </summary>
public static class BattleTypeExtensions
{
    /// <summary>
    /// 把游戏写出的 matchGroup 字符串转成枚举。
    /// 无法识别时返回 <see cref="BattleType.Unknown"/>，不抛异常。
    /// </summary>
    public static BattleType FromMatchGroup(string? matchGroup)
    {
        if (string.IsNullOrWhiteSpace(matchGroup))
        {
            return BattleType.Unknown;
        }

        return matchGroup.Trim().ToUpperInvariant() switch
        {
            "RANDOM" or "RANDOM_BATTLE" => BattleType.Random,
            "RANKED" or "RANKED_BATTLE" => BattleType.Ranked,
            "COOP" or "PVE" or "COOPERATIVE" => BattleType.Coop,
            "TRAINING" or "PRIVATE" => BattleType.Training,
            "CLAN" or "CLAN_BATTLE" => BattleType.Clan,
            "OPERATION" or "SCENARIO" => BattleType.Operation,
            _ => BattleType.Unknown,
        };
    }

    /// <summary>把枚举转回稳定的存储字符串。</summary>
    public static string ToStorageString(this BattleType type) => type.ToString();
}
