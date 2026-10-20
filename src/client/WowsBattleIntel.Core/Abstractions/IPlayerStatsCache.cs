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
    /// 一次取出一批玩家的账号级与单船级缓存快照。
    /// </summary>
    /// <remarks>
    /// 逐玩家调用 <see cref="GetAccountSnapshotAsync"/> 与
    /// <see cref="GetShipSnapshotAsync"/> 会让一局名单产生上百次串行往返，
    /// 查询延迟随人数线性增长。批量口径与逐个查询完全一致：账号层取 TTL 内
    /// 最新一条，单船层只返回带该船数据的最新一条。未命中或超期的玩家不在
    /// 结果里，调用方按无缓存处理。
    ///
    /// 默认实现按逐个查询组装，不改变任何已有实现的行为；具备批量能力的
    /// 实现应重写它，把往返压成一次。
    /// </remarks>
    /// <param name="requests">玩家账号、服务器与本局船只。</param>
    /// <param name="maxAge">缓存有效期，语义同 <see cref="GetAccountSnapshotAsync"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与 requests 一一对应的快照，账号层放在 Account，单船层放在 Ship。</returns>
    async Task<IReadOnlyDictionary<PlayerSnapshotKey, PlayerSnapshotPair>> GetSnapshotPairsAsync(
        IReadOnlyList<PlayerSnapshotRequest> requests,
        TimeSpan maxAge,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<PlayerSnapshotKey, PlayerSnapshotPair>();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var account = await GetAccountSnapshotAsync(
                request.AccountId, request.Server, maxAge, cancellationToken).ConfigureAwait(false);
            PlayerSnapshot? ship = null;
            if (!string.IsNullOrEmpty(request.ShipId))
            {
                ship = await GetShipSnapshotAsync(
                    request.AccountId, request.Server, request.ShipId, maxAge, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (account is not null || ship is not null)
            {
                result[new PlayerSnapshotKey(request.AccountId, request.Server, request.ShipId)]
                    = new PlayerSnapshotPair(account, ship);
            }
        }

        return result;
    }

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

/// <summary>
/// 批量取快照时的查询键。
/// </summary>
/// <param name="AccountId">账号 ID。</param>
/// <param name="Server">服务器。</param>
/// <param name="ShipId">本局船只，空串表示只查账号层。</param>
public readonly record struct PlayerSnapshotKey(string AccountId, GameServer Server, string ShipId);

/// <summary>
/// 批量取快照时的查询条件。
/// </summary>
/// <param name="AccountId">账号 ID。</param>
/// <param name="Server">服务器。</param>
/// <param name="ShipId">本局船只，空串表示只查账号层。</param>
public readonly record struct PlayerSnapshotRequest(string AccountId, GameServer Server, string ShipId);

/// <summary>
/// 一个键对应的两层快照，任一为空表示该层未命中。
/// </summary>
/// <param name="Account">账号级快照。</param>
/// <param name="Ship">单船级快照。</param>
public sealed record PlayerSnapshotPair(PlayerSnapshot? Account, PlayerSnapshot? Ship);
