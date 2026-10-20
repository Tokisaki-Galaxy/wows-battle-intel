using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 玩家战绩的统一入口：一次推进整批，逐块落库。
/// </summary>
/// <remarks>
/// 全应用的战绩来源只走这里，避免各调用点各写一套。赛前名单、历史对局详情、
/// 重算链路都调用本服务。
///
/// 缓存能力不在这一层：提供商由 <see cref="Stats.CachingPlayerStatsProvider"/>
/// 包装，查缓存、落库、TTL 判定都在装饰器里完成。
///
/// 整批一次交给提供商，不再按固定人数切块：账号统计与军团标签本身就是按
/// 100 个账号的批量端点，切块只会把同一局拆成多轮同样的请求；单船统计的
/// 有界并发也随之摊平到一次，不会每块重建一次信号量。提供商内部按批落库，
/// 中途被取消时已拿到的数据照样持久化，"取消不丢已完成部分"的保证不变。
/// </remarks>
public sealed class PlayerStatsService
{
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
    /// 为一批玩家拉取统计，拉到即落库缓存。
    /// </summary>
    /// <remarks>
    /// 只处理真人。账号 ID 为 -1 哨兵的赛前玩家也照收：装饰器或提供商内部会
    /// 先用本地历史把昵称换成真账号，省一次 account/list 请求，也能让账号级
    /// 快照直接命中缓存。
    ///
    /// 整批失败记日志后返回，不向上抛非取消除外；取消时直接抛出，
    /// 已落库的数据不受影响。
    /// </remarks>
    /// <param name="players">待补全的玩家，统计就地写回。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="onProgress">每落一批数据的回调，用于把已到的结果刷上界面。可为空。</param>
    /// <returns>本次拉取的统计结果，含成功处理的条数与失败批次。</returns>
    public async Task<StatsFetchOutcome> FetchAndCacheAsync(
        IReadOnlyList<PlayerRecord> players,
        CancellationToken cancellationToken = default,
        Action? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(players);

        // 隐藏标记由提供商标记后落库，后续轮次不再重复请求；
        // 尚未标记的玩家照常进入请求，由提供商回写 IsHidden。
        var targets = players
            .Where(p => p.IsStatsCandidate)
            .ToList();
        if (targets.Count == 0)
        {
            return new StatsFetchOutcome(0, 0, 0);
        }

        try
        {
            if (onProgress is not null && provider is IProgressStatsFetch progress)
            {
                await progress
                    .FetchStatsAsync(targets, cancellationToken, onProgress)
                    .ConfigureAwait(false);
            }
            else
            {
                await provider.FetchStatsAsync(targets, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 装饰器已在取消时先落已拿到数据的部分，这里让取消向上传播。
            throw;
        }
        catch (Exception ex)
        {
            // 失败不影响已落库的数据，交给调用方按"零处理"判定。
            logs.Warn($"战绩拉取失败: {ex.Message}");
            return new StatsFetchOutcome(0, 1, 1);
        }

        return new StatsFetchOutcome(targets.Count, 0, 1);
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
