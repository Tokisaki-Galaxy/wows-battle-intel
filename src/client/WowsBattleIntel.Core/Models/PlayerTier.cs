namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 玩家水平档位，由账号胜率与场次判定。
/// </summary>
/// <remarks>
/// 阈值口径见 <see cref="Scoring.TeamAggregator"/>。界面文案与着色只读这个
/// 枚举，不在各自代码里重写阈值。
/// </remarks>
public enum PlayerTier
{
    /// <summary>未知，统计缺失或隐藏战绩。</summary>
    Unknown = 0,

    /// <summary>低于海王阈值的常规玩家。</summary>
    Normal,

    /// <summary>低水平玩家，胜率不高于猴子阈值。</summary>
    Ape,

    /// <summary>高水平玩家，胜率高于海王阈值且场次达标。</summary>
    Unicum,
}
