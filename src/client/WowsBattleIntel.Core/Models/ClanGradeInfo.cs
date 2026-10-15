namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 军团强度分档。
/// </summary>
/// <remarks>
/// WG 公开 API 已下线领土战评分字段，clans/info 请求 loading_battles
/// 返回 407 INVALID_FIELDS，ApeRadar 源码里也没有军团分档逻辑可对齐。
/// 因此采用成员账号 PvP 胜率聚合口径：拉军团成员列表，批量取账号
/// 统计，按总胜场除以总场次聚合，阈值见 Stats 层的 ClanGrading。
/// </remarks>
public enum ClanGradeTier
{
    /// <summary>普通军团，不标记。</summary>
    Normal = 0,

    /// <summary>强会，蓝色标记。</summary>
    Good = 1,

    /// <summary>顶级强会，俗称紫会，紫色标记。</summary>
    Unicum = 2,
}

/// <summary>
/// 军团分档信息，供界面着色与 ToolTip 展示。
/// </summary>
public sealed class ClanGradeInfo
{
    /// <summary>军团标签，不含方括号。</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>军团全名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>军团成员数。</summary>
    public int MembersCount { get; set; }

    /// <summary>强度分档。</summary>
    public ClanGradeTier Grade { get; set; } = ClanGradeTier.Normal;

    /// <summary>成员聚合胜率，0 到 1。-1 表示样本不足或查询失败，不可用。</summary>
    public double AggregateWinrate { get; set; } = -1;

    /// <summary>参与聚合的可见成员数，隐藏战绩的成员不计入。</summary>
    public int VisibleMembers { get; set; }

    /// <summary>
    /// 分档对应的颜色资源键，经 BrushKeyConverter 换成画刷。
    /// 紫会用 UnicumBrush，蓝会用 PrimaryFgBrush，普通不给键。
    /// </summary>
    public string ColorKey => Grade switch
    {
        ClanGradeTier.Unicum => "UnicumBrush",
        ClanGradeTier.Good => "PrimaryFgBrush",
        _ => string.Empty,
    };
}
