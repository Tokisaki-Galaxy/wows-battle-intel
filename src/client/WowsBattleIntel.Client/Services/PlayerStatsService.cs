using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 玩家战绩的统一入口：分块推进拉取。
/// </summary>
/// <remarks>
/// 全应用的战绩来源只走这里，避免各调用点各写一套。赛前名单、历史对局详情、
/// 重算链路都调用本服务。
///
/// 缓存能力不在这一层：提供商由 <see cref="Stats.CachingPlayerStatsProvider"/>
/// 包装，查缓存、落库、TTL 判定都在装饰器里完成。本服务只负责一件事——把一大批
/// 玩家切成小块逐块推进，让拉取过程可被中断、可增量展示。
///
/// 分块推进是刻意的：加载界面里 tempArenaInfo 会随玩家陆续加入反复重写，
/// 每次都触发新一轮详情加载并取消上一轮拉取。若只在整批拉完后一次性推进，
/// 被取消的那批已经拿到的数据会全部丢掉。按小块拉取、每块拉完立即让装饰器
/// 落库，任何时刻被取消，已完成的块都已持久化。
/// </remarks>
public sealed class PlayerStatsService
{
    /// <summary>
    /// 每次拉取的玩家数。取值兼顾请求效率与落库粒度：
    /// 账号统计本就按 100 批量，单船统计逐玩家，块小只会多几次账号批量请求。
    /// </summary>
    private const int BatchSize = 8;

    private readonly IPlayerStatsProvider provider;
    private readonly LogService logs;

    /// <summary>
    /// 构造服务。
    /// </summary>
    public PlayerStatsService(
        IPlayerStatsProvider provider,
        LogService logs)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(logs);

        this.provider = provider;
        this.logs = logs;
    }

    /// <summary>
    /// 为一批玩家分块拉取统计，逐块落库缓存。
    /// </summary>
    /// <remarks>
    /// 只处理真人。账号 ID 为 -1 哨兵的赛前玩家也照收：装饰器或提供商内部会
    /// 先用本地历史把昵称换成真账号，省一次 account/list 请求，也能让账号级
    /// 快照直接命中缓存。
    ///
    /// 单块拉取失败记日志后跳过该块，不中断其余块。取消时直接抛出，
    /// 已落库的块不受影响。
    /// </remarks>
    /// <param name="players">待补全的玩家，统计就地写回。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次拉取的统计结果，含成功处理的条数与失败批次。</returns>
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

        int succeeded = 0;
        int failed = 0;
        int total = 0;
        for (int offset = 0; offset < targets.Count; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = targets.Skip(offset).Take(BatchSize).ToList();
            total++;
            try
            {
                await provider.FetchStatsAsync(chunk, cancellationToken).ConfigureAwait(false);
                succeeded += chunk.Count;
            }
            catch (OperationCanceledException)
            {
                // 装饰器已在取消时先落已拿到数据的部分，这里让取消向上传播。
                throw;
            }
            catch (Exception ex)
            {
                // 单块失败不影响其余块，已拿到的数据照常落库。
                failed++;
                logs.Warn($"战绩拉取失败（第 {offset / BatchSize + 1} 批）: {ex.Message}");
            }
        }

        return new StatsFetchOutcome(succeeded, failed, total);
    }
}

/// <summary>
/// 一次战绩拉取的结果。
/// </summary>
/// <param name="Processed">成功处理（拉取或命中缓存）的玩家数。</param>
/// <param name="FailedBatches">失败的批次数。</param>
/// <param name="TotalBatches">总批次数。</param>
public sealed record StatsFetchOutcome(int Processed, int FailedBatches, int TotalBatches)
{
    /// <summary>是否全部批次都失败。用于连续失败判定与红色提醒。</summary>
    public bool AllFailed => TotalBatches > 0 && FailedBatches == TotalBatches;
}
