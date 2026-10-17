using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Abstractions;

/// <summary>
/// 玩家战绩的本地缓存。供统计提供商在联网前查缓存、联网后回写。
/// </summary>
/// <remarks>
/// 与 <see cref="IPlayerSnapshotStore"/> 的区别在于语义：快照存储服务的是
/// 特征工程与训练，按时间上界取"对局之前"的数据，不关心新鲜度；本接口服务
/// 的是展示层的加速查询，只认 TTL 内的新数据，过期视为未命中。
///
/// 缓存读失败、超时、无记录一律返回 null，由调用方回落网络。缓存只是加速，
/// 任何一环失效都不能让战绩查询整体失败。
/// </remarks>
public interface IPlayerStatsCache
{
    /// <summary>
    /// 按昵称批量解析真实账号 ID。命中本地历史即省一次账号解析请求。
    /// </summary>
    /// <returns>昵称到账号 ID 的映射，查不到的昵称不在其中。</returns>
    Task<Dictionary<string, string>> GetAccountIdsByNamesAsync(
        IReadOnlyList<string> playerNames,
        GameServer server,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取账号级缓存快照。只返回 maxAge 以内的最新一条。
    /// </summary>
    /// <remarks>
    /// maxAge 为 <see cref="TimeSpan.MaxValue"/> 时不设新鲜度上界，取任意最新一条。
    /// </remarks>
    Task<PlayerSnapshot?> GetAccountSnapshotAsync(
        string accountId,
        GameServer server,
        TimeSpan maxAge,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取指定船只的缓存快照。只返回 maxAge 以内的最新一条带单船数据的记录。
    /// </summary>
    /// <remarks>
    /// maxAge 语义同 <see cref="GetAccountSnapshotAsync"/>。
    /// </remarks>
    Task<PlayerSnapshot?> GetShipSnapshotAsync(
        string accountId,
        GameServer server,
        string shipId,
        TimeSpan maxAge,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量写入快照缓存。
    /// </summary>
    Task SaveSnapshotsAsync(
        IReadOnlyList<PlayerSnapshot> snapshots,
        CancellationToken cancellationToken = default);
}
