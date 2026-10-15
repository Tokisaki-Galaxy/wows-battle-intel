using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Stats;

/// <summary>
/// 军团强度分档阈值与判定。
/// </summary>
/// <remarks>
/// 口径说明：WG 公开 API 已下线领土战评分字段（clans/info 请求
/// loading_battles 返回 407 INVALID_FIELDS），ApeRadar 源码里也没有
/// 军团分档逻辑可对齐，其紫色画刷只用于 WatchStatus 玩家标记。
/// 因此自定口径：军团成员账号 PvP 总胜场除以总场次得到聚合胜率，
/// 按固定阈值分档。阈值参考账号胜率分档（0.58 海王线）下移：
/// 军团聚合了数十名成员，均值向服务器均值回归，强会线必须低于个人线。
/// 实测样本：CCDI 聚合 0.552、ROCM 0.496、BCAT 0.489。
/// </remarks>
public static class ClanGrading
{
    /// <summary>可见成员少于该值不评档，小样本聚合无意义。</summary>
    public const int MinVisibleMembers = 5;

    /// <summary>成员总场次少于该值不评档，整体样本太薄。</summary>
    public const double MinTotalBattles = 5000;

    /// <summary>聚合胜率达到该值为顶级强会，俗称紫会。</summary>
    public const double UnicumWinrateThreshold = 0.55;

    /// <summary>聚合胜率达到该值为强会。</summary>
    public const double GoodWinrateThreshold = 0.52;

    /// <summary>
    /// 按聚合胜率与样本规模分档。
    /// </summary>
    public static ClanGradeTier Grade(double aggregateWinrate, int visibleMembers, double totalBattles)
    {
        if (aggregateWinrate < 0
            || visibleMembers < MinVisibleMembers
            || totalBattles < MinTotalBattles)
        {
            return ClanGradeTier.Normal;
        }

        if (aggregateWinrate >= UnicumWinrateThreshold)
        {
            return ClanGradeTier.Unicum;
        }

        return aggregateWinrate >= GoodWinrateThreshold ? ClanGradeTier.Good : ClanGradeTier.Normal;
    }
}
