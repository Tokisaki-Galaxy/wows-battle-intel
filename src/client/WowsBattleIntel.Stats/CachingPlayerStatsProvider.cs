using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Stats;

/// <summary>
/// 统计提供商的缓存装饰器：联网前查本地缓存，联网后回写。
/// </summary>
/// <remarks>
/// 把"缓存加速"从应用服务里抽出来，做成 <see cref="IPlayerStatsProvider"/> 的
/// 一层包装。这样任何具体提供商（WG 公开 API、未来的其他源）套上它都自动获得
/// 缓存能力，调用方只依赖接口，不需要各自重复实现查缓存与落库。
///
/// 命中判据是"账号胜率、分舰种、单船三项都齐"，与界面补拉口径一致：只拉账号
/// 胜率会让舰种与单船列永远留空。缓存有效期由 <see cref="PlayerStatsCacheOptions.Ttl"/>
/// 控制，超期视为未命中重新联网，避免玩家当前战绩长期停留在旧值。
///
/// 开关只控制"读缓存加速"，不控制"写快照"：快照是近期状态特征与训练的原料，
/// 无论是否开启加速都必须持续累积，否则关掉加速会连带把训练数据断掉。
///
/// 缓存读写失败、缓存仓储缺失一律回落网络，不让加速通道拖垮战绩查询本身。
/// </remarks>
public sealed class CachingPlayerStatsProvider : IPlayerStatsProvider, IForcedStatsFetch
{
    private readonly IPlayerStatsProvider inner;
    private readonly IPlayerStatsCache? cache;
    private readonly PlayerStatsCacheOptions options;

    /// <summary>
    /// 构造装饰器。
    /// </summary>
    /// <param name="inner">被包装的真实提供商，负责实际网络拉取。</param>
    /// <param name="cache">本地缓存仓储。为空时退化为纯透传。</param>
    /// <param name="options">缓存开关与有效期。</param>
    public CachingPlayerStatsProvider(
        IPlayerStatsProvider inner,
        IPlayerStatsCache? cache,
        PlayerStatsCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);

        this.inner = inner;
        this.cache = cache;
        this.options = options;
    }

    /// <inheritdoc />
    public string Name => $"CACHED({inner.Name})";

    /// <summary>被包装的提供商，供上层识别真实数据源。</summary>
    public IPlayerStatsProvider Inner => inner;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlayerRecord>> FetchStatsAsync(
        IReadOnlyList<PlayerRecord> players,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(players);

        // 没有缓存仓储时纯透传，行为与直接持有 inner 完全一致。
        if (cache is null)
        {
            return await inner.FetchStatsAsync(players, cancellationToken).ConfigureAwait(false);
        }

        return await FetchStatsCoreAsync(players, bypassCache: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlayerRecord>> ForceFetchStatsAsync(
        IReadOnlyList<PlayerRecord> players,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(players);

        // 用户主动刷新要的是最新数据：跳过缓存命中，但仍走本层统一落库，
        // 拉到的快照照常写回，之后的普通查询继续享受缓存加速。
        if (cache is null)
        {
            return await inner.FetchStatsAsync(players, cancellationToken).ConfigureAwait(false);
        }

        return await FetchStatsCoreAsync(players, bypassCache: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 拉取主体。读缓存命中与网络回写都集中在这里，bypassCache 只关掉读缓存。
    /// </summary>
    private async Task<IReadOnlyList<PlayerRecord>> FetchStatsCoreAsync(
        IReadOnlyList<PlayerRecord> players,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        // 隐藏战绩玩家也要交给 inner：他们没有统计，但军团标签仍要落库，
        // 供后续跨局回填。是否缺数据由 IsFullyCovered 判定，隐藏者恒缺。
        var targets = players
            .Where(p => !p.IsBot && !string.IsNullOrEmpty(p.PlayerName))
            .ToList();
        if (targets.Count == 0)
        {
            return await inner.FetchStatsAsync(players, cancellationToken).ConfigureAwait(false);
        }

        // 读缓存受开关控制：关闭时跳过命中，每次都联网取最新。写快照不受开关
        // 影响，始终落库，否则关掉加速的同时会把训练数据的累积一起关掉。
        if (options.Enabled && !bypassCache)
        {
            await ResolveFromLocalHistoryAsync(targets, cancellationToken).ConfigureAwait(false);
            await HydrateFromCacheAsync(targets, cancellationToken).ConfigureAwait(false);
        }

        // 缓存已齐全的玩家不再联网。缺数据的才交回 inner。
        var needNetwork = options.Enabled
            ? targets.Where(p => !p.IsFullyCovered).ToList()
            : targets;
        if (needNetwork.Count == 0)
        {
            return players;
        }

        try
        {
            await inner.FetchStatsAsync(needNetwork, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消前把已经拿到数据的部分落库，避免整批白拉。
            await SafeSaveAsync(needNetwork, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await SafeSaveAsync(needNetwork, cancellationToken).ConfigureAwait(false);

        // inner 就地写回 needNetwork 里的记录，players 是它们的超集，
        // 命中缓存的记录已在上一步灌好，整体返回即可。
        return players;
    }

    /// <summary>
    /// 用本地历史把 -1 哨兵玩家的账号 ID 补上。
    /// </summary>
    /// <remarks>
    /// 命中本地历史即免去一次 account/list 请求，也让后续账号级快照查询直接
    /// 命中缓存。查不到的保持哨兵，交给 inner 按昵称解析。只对服务器已知
    /// （非 Auto）的玩家查，Auto 无法定库表里的 server 过滤。
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
                mapping = await cache!
                    .GetAccountIdsByNamesAsync(
                        group.Select(p => p.PlayerName).ToList(), group.Key, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 本地查询失败不影响在线解析，退回哨兵即可。
                continue;
            }

            foreach (var player in group)
            {
                if (mapping.TryGetValue(player.PlayerName, out string? accountId))
                {
                    player.AccountId = accountId;
                }
            }
        }
    }

    /// <summary>
    /// 从缓存把统计灌回玩家记录。
    /// </summary>
    /// <remarks>
    /// 账号 ID 已确定（本地历史或上一轮解析）才查得到。账号层取有效期内的最新
    /// 快照，分舰种同源于该快照；带本局船 ID 时再取该船最新一条，单船数据可能
    /// 来自更早的一局，这正是缓存要的效果。
    /// </remarks>
    private async Task HydrateFromCacheAsync(
        List<PlayerRecord> targets,
        CancellationToken cancellationToken)
    {
        foreach (var player in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 查询服优先用已知的具体服，玩家记录为 Auto 时退回 StatsServer。
            // 赛前链路把对局服推断成具体服后，玩家记录的 Server 仍是 Auto，
            // 只认 Server 会让这些玩家每次都被判成“无缓存可用”。
            GameServer server = player.Server == GameServer.Auto ? player.StatsServer : player.Server;
            if (player.AccountId == "-1" || server == GameServer.Auto)
            {
                continue;
            }

            try
            {
                var account = await cache!
                    .GetAccountSnapshotAsync(player.AccountId, server, options.Ttl, cancellationToken)
                    .ConfigureAwait(false);
                if (account is null)
                {
                    continue;
                }

                ApplyAccountSnapshot(player, account);
                player.StatsServer = server;

                // 单船层：带本局船 ID 时按船查，命中才覆盖。
                if (!string.IsNullOrEmpty(player.ShipId))
                {
                    var ship = await cache
                        .GetShipSnapshotAsync(
                            player.AccountId, server, player.ShipId, options.Ttl, cancellationToken)
                        .ConfigureAwait(false);
                    if (ship is not null)
                    {
                        player.ShipBattles = ship.ShipBattles;
                        player.ShipWinrate = ship.ShipWinrate;
                        player.ShipAvgDamage = ship.ShipAvgDamage;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 缓存查询失败不影响后续网络补拉。
            }
        }
    }

    /// <summary>
    /// 把账号级快照字段并入玩家记录。
    /// </summary>
    private static void ApplyAccountSnapshot(PlayerRecord player, PlayerSnapshot account)
    {
        // 隐藏快照没有统计，只带回军团标签。把它当统计灌进去会把账号胜率
        // 从 -1 变成快照里的 0，界面上就没有隐藏战绩只有 0.0% 了。
        if (!account.HasStats)
        {
            ApplyClanTag(player, account);
            return;
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

        ApplyClanTag(player, account);
    }

    /// <summary>
    /// 军团标签只补空值。回放链路已带回放里的标签，不被缓存覆盖。
    /// </summary>
    private static void ApplyClanTag(PlayerRecord player, PlayerSnapshot account)
    {
        if (string.IsNullOrEmpty(player.ClanTag) && !string.IsNullOrEmpty(account.ClanTag))
        {
            player.ClanTag = account.ClanTag;
        }
    }

    /// <summary>
    /// 把已带统计的玩家写入缓存，异常静默。
    /// </summary>
    /// <remarks>
    /// 隐藏战绩或无统计的玩家原本跳过不写空快照。但军团标签是身份字段，
    /// 隐藏战绩也查得到，且赛前 tempArenaInfo 不含标签，需要靠缓存跨局回填。
    /// 因此带军团标签的记录即使没有统计也落一条，统计字段保持 -1 哨兵。
    /// </remarks>
    private async Task SafeSaveAsync(
        IReadOnlyList<PlayerRecord> players,
        CancellationToken cancellationToken)
    {
        if (cache is null)
        {
            return;
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var snapshots = players
            .Where(p => (p.HasStats || !string.IsNullOrEmpty(p.ClanTag))
                && p.AccountId != "-1"
                && p.AccountId.Length > 0)
            .Select(p => PlayerSnapshot.FromPlayer(p, capturedAt))
            .ToList();

        if (snapshots.Count == 0)
        {
            return;
        }

        try
        {
            await cache.SaveSnapshotsAsync(snapshots, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 落库失败只影响下次命中，不影响本次查询结果。
        }
    }
}

/// <summary>
/// 战绩缓存装饰器的开关与有效期。
/// </summary>
public sealed class PlayerStatsCacheOptions
{
    /// <summary>
    /// 是否启用缓存加速查询。关闭时每次都联网取最新，但快照仍照常落库。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 缓存有效期。默认 7 天。为 <see cref="TimeSpan.MaxValue"/> 时不设新鲜度
    /// 上界，取任意最新一条。
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromDays(7);
}
