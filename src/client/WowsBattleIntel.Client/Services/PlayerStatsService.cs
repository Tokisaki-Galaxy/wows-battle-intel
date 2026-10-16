using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Storage;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 玩家战绩的统一入口：拉取并落库缓存。
/// </summary>
/// <remarks>
/// 全应用的战绩来源只走这里，避免各调用点各写一套。赛前名单、历史对局详情、
/// 重算链路都调用本服务，拉到即落快照，下次任何路径再遇到同一玩家直接命中
/// 缓存，不再重复请求 WG API。
///
/// 分块推进是刻意的：加载界面里 tempArenaInfo 会随玩家陆续加入反复重写，
/// 每次都触发新一轮详情加载并取消上一轮拉取。若只在整批拉完后一次性落库，
/// 被取消的那批已经拿到的数据会全部丢掉，数据库里始终是空的。按小块拉取、
/// 每块拉完立即落库，任何时刻被取消，已完成的块都已持久化。
/// </remarks>
public sealed class PlayerStatsService
{
    /// <summary>
    /// 每次拉取并落库的玩家数。取值兼顾请求效率与落库粒度：
    /// 账号统计本就按 100 批量，单船统计逐玩家，块小只会多几次账号批量请求。
    /// </summary>
    private const int BatchSize = 8;

    private readonly IPlayerStatsProvider provider;
    private readonly SqliteBattleRepository repository;
    private readonly LogService logs;

    /// <summary>
    /// 构造服务。
    /// </summary>
    public PlayerStatsService(
        IPlayerStatsProvider provider,
        SqliteBattleRepository repository,
        LogService logs)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logs);

        this.provider = provider;
        this.repository = repository;
        this.logs = logs;
    }

    /// <summary>
    /// 为一批玩家拉取统计并落库缓存。
    /// </summary>
    /// <remarks>
    /// 只处理真人。账号 ID 为 -1 哨兵的赛前玩家也照收：先用本地历史把昵称
    /// 换成真账号，省一次 account/list 请求，也能让账号级快照直接命中缓存；
    /// 本地没有记录的再由提供商按昵称解析。提供商内部对 Auto 服跳过。
    ///
    /// 命中缓存：账号与船 ID 确定后，先查库里的最新快照，把已有数据直接
    /// 灌回玩家记录。已经齐全的玩家不再请求 WG API，只有真正缺数据的才
    /// 走网络。tempArenaInfo 每局重写都会重建名单对象，这一步保证同一批
    /// 对手不会每局都重新拉一遍。
    ///
    /// 单块拉取失败记日志后跳过该块，不中断其余块。取消时直接抛出，
    /// 已落库的块不受影响。
    /// </remarks>
    /// <param name="players">待补全的玩家，统计就地写回。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次拉取的统计结果，含成功落库条数与失败批次。</returns>
    public async Task<StatsFetchOutcome> FetchAndCacheAsync(
        IReadOnlyList<PlayerRecord> players,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(players);

        var targets = players
            .Where(p => !p.IsBot && !string.IsNullOrEmpty(p.PlayerName))
            .ToList();
        if (targets.Count == 0)
        {
            return new StatsFetchOutcome(0, 0, 0);
        }

        await ResolveFromLocalHistoryAsync(targets, cancellationToken).ConfigureAwait(false);
        int cacheHits = await HydrateFromCacheAsync(targets, cancellationToken).ConfigureAwait(false);

        // 缓存已齐全的玩家不必再走网络。
        var needNetwork = targets
            .Where(p => p.AccountWinrate < 0
                || p.ShipBattles < 0
                || string.IsNullOrEmpty(p.ClassStatsJson)
                || p.ClassStatsJson.Length <= 2)
            .ToList();
        if (needNetwork.Count == 0)
        {
            logs.Info($"战绩全部命中缓存 {cacheHits}/{targets.Count} 名，跳过网络请求");
            return new StatsFetchOutcome(cacheHits, 0, 0);
        }

        int cached = 0;
        int failed = 0;
        int total = 0;
        for (int offset = 0; offset < needNetwork.Count; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = needNetwork.Skip(offset).Take(BatchSize).ToList();
            total++;
            try
            {
                await provider.FetchStatsAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 先落已拿到数据的部分，再让取消向上传播。
                cached += await CacheAsync(chunk, cancellationToken: default).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                // 单块失败不影响其余块，已拿到的数据照常落库。
                failed++;
                logs.Warn($"战绩拉取失败（第 {offset / BatchSize + 1} 批）: {ex.Message}");
            }

            cached += await CacheAsync(chunk, cancellationToken).ConfigureAwait(false);
        }

        return new StatsFetchOutcome(cached, failed, total);
    }

    /// <summary>
    /// 从库里已有快照把统计灌回玩家记录，返回命中人数。
    /// </summary>
    /// <remarks>
    /// 账号 ID 已确定（本地历史或上一轮解析）才查得到。账号层取最新快照，
    /// 分舰种与单船层同样取自该快照；带本局船 ID 时再取该船最新一条，
    /// 单船数据可能来自更早的一局，这正是缓存要的效果。
    /// </remarks>
    private async Task<int> HydrateFromCacheAsync(
        List<PlayerRecord> targets,
        CancellationToken cancellationToken)
    {
        int hits = 0;
        foreach (var player in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (player.AccountId == "-1" || player.Server == GameServer.Auto)
            {
                continue;
            }

            try
            {
                var account = await repository
                    .GetLatestSnapshotAsync(player.AccountId, player.Server, cancellationToken)
                    .ConfigureAwait(false);
                if (account is null)
                {
                    continue;
                }

                player.AccountBattles = account.AccountBattles;
                player.AccountWinrate = account.AccountWinrate;
                player.AccountAvgDamage = account.AccountAvgDamage;
                player.AccountBattlesSolo = account.AccountBattlesSolo;
                player.AccountWinrateSolo = account.AccountWinrateSolo;
                player.AccountBattlesDiv2 = account.AccountBattlesDiv2;
                player.AccountWinrateDiv2 = account.AccountWinrateDiv2;
                player.AccountBattlesDiv3 = account.AccountBattlesDiv3;
                player.AccountWinrateDiv3 = account.AccountWinrateDiv3;
                player.WeightedWinrate = account.WeightedWinrate;
                player.ClassStatsJson = account.ClassStatsJson ?? string.Empty;
                if (account.FavoriteTier >= 0)
                {
                    player.FavoriteTier = account.FavoriteTier;
                }

                // 单船层：带本局船 ID 时按船查，命中才覆盖。
                if (!string.IsNullOrEmpty(player.ShipId))
                {
                    var ship = await repository
                        .GetLatestShipSnapshotAsync(player.AccountId, player.Server, player.ShipId, cancellationToken)
                        .ConfigureAwait(false);
                    if (ship is not null)
                    {
                        player.ShipBattles = ship.ShipBattles;
                        player.ShipWinrate = ship.ShipWinrate;
                        player.ShipAvgDamage = ship.ShipAvgDamage;
                    }
                }

                hits++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 缓存查询失败不影响后续网络补拉。
                logs.Warn($"缓存命中查询失败: {ex.Message}");
            }
        }

        if (hits > 0)
        {
            logs.Info($"战绩命中缓存 {hits}/{targets.Count} 名");
        }

        return hits;
    }

    /// <summary>
    /// 用本地历史把 -1 哨兵玩家的账号 ID 补上。
    /// </summary>
    /// <remarks>
    /// 命中本地历史即免去一次 account/list 请求，也让后续账号级快照查询
    /// 直接命中缓存。查不到的玩家保持哨兵，交给提供商按昵称解析。
    /// 只对服务器已知（非 Auto）的玩家查，Auto 无法定库表里的 server 过滤。
    /// </remarks>
    private async Task ResolveFromLocalHistoryAsync(
        List<PlayerRecord> targets,
        CancellationToken cancellationToken)
    {
        var sentinels = targets
            .Where(p => p.AccountId == "-1" && p.Server != GameServer.Auto)
            .ToList();
        if (sentinels.Count == 0)
        {
            return;
        }

        foreach (var group in sentinels.GroupBy(p => p.Server))
        {
            Dictionary<string, string> mapping;
            try
            {
                mapping = await repository
                    .GetAccountIdsByNamesAsync(group.Select(p => p.PlayerName).ToList(), group.Key, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 本地查询失败不影响在线解析，退回哨兵即可。
                logs.Warn($"本地账号解析失败: {ex.Message}");
                continue;
            }

            int hit = 0;
            foreach (var player in group)
            {
                if (mapping.TryGetValue(player.PlayerName, out string? accountId))
                {
                    player.AccountId = accountId;
                    hit++;
                }
            }

            if (hit > 0)
            {
                logs.Info($"本地历史解析账号 {hit}/{group.Count()} 名（{group.Key}）");
            }
        }
    }

    /// <summary>
    /// 把已带统计的玩家写入快照缓存，不发起网络请求。
    /// </summary>
    /// <remarks>
    /// 供已经拿到统计的链路单独落库用。无有效统计（隐藏战绩或查询失败）
    /// 的玩家跳过，不写空快照。
    /// </remarks>
    /// <returns>落库条数。</returns>
    public async Task<int> CacheAsync(
        IReadOnlyList<PlayerRecord> players,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(players);

        var capturedAt = DateTimeOffset.UtcNow;
        var snapshots = players
            .Where(p => p.HasStats && p.AccountId != "-1" && p.AccountId.Length > 0)
            .Select(p => PlayerSnapshot.FromPlayer(p, capturedAt))
            .ToList();

        if (snapshots.Count == 0)
        {
            return 0;
        }

        try
        {
            await repository.SaveSnapshotsAsync(snapshots, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logs.Warn($"快照落库失败: {ex.Message}");
            return 0;
        }

        return snapshots.Count;
    }
}

/// <summary>
/// 一次战绩拉取的结果。
/// </summary>
/// <param name="Cached">成功落库的快照条数。</param>
/// <param name="FailedBatches">失败的批次数。</param>
/// <param name="TotalBatches">总批次数。</param>
public sealed record StatsFetchOutcome(int Cached, int FailedBatches, int TotalBatches)
{
    /// <summary>是否全部批次都失败。用于连续失败判定与红色提醒。</summary>
    public bool AllFailed => TotalBatches > 0 && FailedBatches == TotalBatches;
}
