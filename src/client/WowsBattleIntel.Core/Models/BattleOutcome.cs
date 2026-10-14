namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 一局战斗的结果。
/// </summary>
public enum BattleOutcome
{
    /// <summary>尚未知结果，赛前或未解析回放。</summary>
    Unknown = 0,

    /// <summary>友方获胜。</summary>
    Win,

    /// <summary>友方失败。</summary>
    Loss,

    /// <summary>平局。</summary>
    Draw,
}

/// <summary>
/// 战斗结果与对局结果的转换。
/// </summary>
public static class BattleOutcomeExtensions
{
    /// <summary>
    /// 把布尔胜负转成结果枚举。
    /// </summary>
    public static BattleOutcome FromBoolean(bool? win) => win switch
    {
        true => BattleOutcome.Win,
        false => BattleOutcome.Loss,
        null => BattleOutcome.Unknown,
    };

    /// <summary>
    /// 转成用于二分类校准的目标值。未知返回 null。
    /// </summary>
    public static double? ToBinaryTarget(this BattleOutcome outcome) => outcome switch
    {
        BattleOutcome.Win => 1.0,
        BattleOutcome.Loss => 0.0,
        _ => null,
    };
}
