using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Abstractions;

/// <summary>
/// 玩家统计来源。把参战者名单补全成带统计数据的玩家记录。
/// </summary>
/// <remarks>
/// 对应 ApeRadar 的 IPlayerStatsAdapter 模式。
/// 现有实现：Vortex、WG Public、WG Public via Yuyuko proxy。
/// </remarks>
public interface IPlayerStatsProvider
{
    /// <summary>数据源名称，用于日志与缓存键。</summary>
    string Name { get; }

    /// <summary>
    /// 为一批玩家拉取统计。
    /// </summary>
    /// <param name="players">待补全的玩家，含昵称、服务器、船 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>补全统计后的玩家列表。无法查到的玩家保留负数哨兵值。</returns>
    Task<IReadOnlyList<PlayerRecord>> FetchStatsAsync(
        IReadOnlyList<PlayerRecord> players,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 本地历史数据来源。提供近期状态与会话状态所需的逐场记录。
/// </summary>
/// <remarks>
/// 这是 WG 公开 API 给不了的信息。近期状态特征完全依赖本地与中央库
/// 累积的逐场记录，因此这个接口的存在是特征工程的前提。
/// </remarks>
public interface IPlayerHistoryProvider
{
    /// <summary>
    /// 获取某玩家最近的若干场战绩，按时间倒序。
    /// </summary>
    /// <param name="accountId">账号 ID。</param>
    /// <param name="server">服务器。</param>
    /// <param name="limit">最多返回多少场。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<PlayerBattleHistoryEntry>> GetRecentBattlesAsync(
        string accountId,
        GameServer server,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 单场历史战绩条目，近期状态特征的最小输入单元。
/// </summary>
public sealed class PlayerBattleHistoryEntry
{
    /// <summary>对局标识。</summary>
    public string BattleId { get; set; } = string.Empty;

    /// <summary>开始时间，UTC。</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>是否获胜。</summary>
    public bool Won { get; set; }

    /// <summary>该场造成伤害。</summary>
    public int DamageDealt { get; set; }

    /// <summary>该场使用的船 ID。</summary>
    public string ShipId { get; set; } = string.Empty;
}
