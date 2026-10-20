using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Stats;

/// <summary>
/// WG 公开 API 的玩家统计来源。
/// </summary>
/// <remarks>
/// 对应 ApeRadar 的 WgPublicApiGetPlayersStatistics。三步拉取：
/// 1. account/list 按昵称换账号 ID
/// 2. account/info 拿账号胜率与分模式胜率
/// 3. ships/stats 拿单船数据（逐玩家调用）
///
/// 请求之间插入延迟避免触发限流。失败的玩家保留哨兵值，不阻断整体。
/// </remarks>
public sealed class WgPublicStatsProvider : IPlayerStatsProvider
{
    private readonly HttpClient httpClient;
    private readonly WgPublicOptions options;

    // ship_id 到 WG 舰种字符串的映射，来自 encyclopedia 目录。
    // 惰性加载并缓存，过期时间由 ClassCatalogCacheDuration 控制。
    private Dictionary<string, string> shipClassLookup = new(StringComparer.Ordinal);

    // ship_id 到等级的映射，与舰种映射同源同时加载，供常用等级计算。
    private Dictionary<string, int> shipTierLookup = new(StringComparer.Ordinal);

    private DateTimeOffset shipClassLookupLoadedAt = DateTimeOffset.MinValue;

    /// <summary>目录加载失败后的负缓存截止时刻。MinValue 表示当前可用。</summary>
    private DateTimeOffset shipClassLookupFailedUntil = DateTimeOffset.MinValue;

    /// <summary>目录加载失败后的重试间隔。失败 5 分钟内不再发起整页轮询。</summary>
    private static readonly TimeSpan ClassLookupFailureBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 构造提供者。
    /// </summary>
    public WgPublicStatsProvider(WgPublicOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        this.httpClient = httpClient ?? WgHttp.CreateClient(TimeSpan.FromSeconds(60));
    }

    /// <inheritdoc />
    public string Name => "WG_PUBLIC";

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlayerRecord>> FetchStatsAsync(
        IReadOnlyList<PlayerRecord> players,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(players);

        // 只处理真人玩家。机器人昵称以冒号开头且有真实账号 ID 两种情况都要排除。
        var targets = players
            .Where(p => !p.IsBot && !string.IsNullOrEmpty(p.PlayerName) && !p.PlayerName.StartsWith(':'))
            .ToList();

        if (targets.Count == 0)
        {
            return players;
        }

        // 按服务器分组，不同服的 API 域名不同。分组键用快照口径：赛前名单
        // 的 Server 恒为 Auto，真服只在 StatsServer 上。只认 Server 会让
        // 手动刷新这类把合并后记录传回来的调用整组落进 Auto 被跳过，
        // 表现为"刷新成功"实际一个请求都没发。
        foreach (var group in targets.GroupBy(p => p.SnapshotServer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Key == GameServer.Auto)
            {
                // Auto 无法确定 API 域名，跳过。调用方应在上游解析出具体
                // 服务器（如赛前链路从最近对局推断）。
                continue;
            }

            foreach (var player in group)
            {
                // 服务端确实按这个服发的请求，写回玩家记录：快照按该服落库，
                // 后续合并快照要用同一口径查，不能一直停在 Auto 上。
                player.StatsServer = group.Key;
            }

            await FetchGroupAsync(group.ToList(), group.Key, cancellationToken).ConfigureAwait(false);
        }

        return players;
    }

    /// <summary>
    /// 拉取一个服务器分组内所有玩家的统计。
    /// </summary>
    private async Task FetchGroupAsync(
        List<PlayerRecord> group,
        GameServer server,
        CancellationToken cancellationToken)
    {
        string host = WgApiParser.GetApiHost(server);

        // --- 步骤一：昵称换账号 ID ---
        // 回放解析出的玩家已带真实账号 ID，无需按昵称查询。
        // 只对缺失 ID 的玩家走这一步，省一次请求也避开重名歧义。
        var needResolve = group.Where(p => p.AccountId == "-1").ToList();
        if (needResolve.Count > 0)
        {
            await ResolveAccountIdsAsync(needResolve, host, cancellationToken).ConfigureAwait(false);
        }

        var resolved = group.Where(p => p.AccountId != "-1").ToList();
        if (resolved.Count == 0)
        {
            return;
        }

        // --- 账号统计、军团标签与单船统计相互独立，并发推进。 ---
        // 串行时三段依次排队，单船逐玩家 24 次请求排在最后，整链路拖到
        // 30 秒开外；并发后整链路耗时压到最慢一段的量级。军团查询先于
        // 单船没有依赖，单船数据也不依赖账号统计的结果。
        var accountTask = FetchAccountStatsAsync(resolved, host, cancellationToken);

        Task? clanTask = null;
        if (options.FetchClanTags)
        {
            var needClan = resolved.Where(p => string.IsNullOrEmpty(p.ClanTag)).ToList();
            if (needClan.Count > 0)
            {
                clanTask = FetchPlayerClansAsync(needClan, host, cancellationToken);
            }
        }

        Task? shipTask = null;
        if (!options.SkipShipStats)
        {
            if (options.FetchClassStats)
            {
                // 全船只列表与单船查询同 endpoint，一次请求同时覆盖
                // 单船统计与分舰种聚合，请求数不变。舰种映射先就绪。
                shipTask = Task.Run(async () =>
                {
                    await EnsureShipClassLookupAsync(host, cancellationToken).ConfigureAwait(false);
                    await FetchAllShipStatsAsync(resolved, host, cancellationToken).ConfigureAwait(false);
                }, cancellationToken);
            }
            else
            {
                shipTask = FetchShipStatsAsync(resolved, host, cancellationToken);
            }
        }

        await accountTask.ConfigureAwait(false);
        if (clanTask is not null)
        {
            await clanTask.ConfigureAwait(false);
        }

        if (shipTask is not null)
        {
            await shipTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 昵称换账号 ID 的并发度。端点逐个昵称查询，取略高于单船统计的并发，
    /// 在限流闸门兜底下把前置等待压到最低。
    /// </summary>
    private const int ResolveConcurrency = 6;

    /// <summary>
    /// 用昵称批量换账号 ID。
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveAccountIdsAsync(
        List<PlayerRecord> group,
        string host,
        CancellationToken cancellationToken)
    {
        // 逐昵称查询。WG 的 account/list 对逗号分隔多昵称的批量精确搜索
        // 在部分服务器（Asia 实测）只返回部分结果，逐个查才可靠。
        // 并发逐名解析：名单前置的串行等待对整局查询是最长的一段，24 人
        // 串行时纯等待就有数秒。限流交给 WgHttp 的共享闸门兜底，撞上 429
        // 时全体暂停后再继续，比预先串行牺牲延迟更划算。
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = group
            .Select(p => p.PlayerName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var resolved = new (string Name, IReadOnlyDictionary<string, string> Parsed)?[names.Count];
        var tasks = new List<Task>(names.Count);
        using var gate = new SemaphoreSlim(ResolveConcurrency, ResolveConcurrency);
        for (int i = 0; i < names.Count; i++)
        {
            int index = i;
            string name = names[i];
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    string url = $"https://{host}/wows/account/list/"
                        + $"?application_id={options.ApplicationId}&type=exact&search={Uri.EscapeDataString(name)}";

                    string json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                    resolved[index] = (name, WgApiParser.ParseAccountIds(json));
                }
                catch (OperationCanceledException)
                {
                    // 取消由外层 Task.WhenAll 统一向上抛。
                }
                catch (Exception)
                {
                    // 单个昵称失败不影响其他玩家。
                }
                finally
                {
                    gate.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var entry in resolved)
        {
            if (entry is not { } value)
            {
                continue;
            }

            foreach (var kv in value.Parsed)
            {
                mapping[kv.Key] = kv.Value;
            }
        }

        foreach (var player in group)
        {
            if (mapping.TryGetValue(player.PlayerName, out string? accountId))
            {
                player.AccountId = accountId;
            }
        }

        return mapping;
    }

    /// <summary>
    /// 拉取账号胜率与分模式胜率。
    /// </summary>
    /// <remarks>
    /// WG API 单次请求的 account_id 数量有上限，通常 100 个。超过会报错或
    /// 静默截断，因此必须分批。每批之间插入延迟避免触发限流。
    /// </remarks>
    private async Task FetchAccountStatsAsync(
        List<PlayerRecord> group,
        string host,
        CancellationToken cancellationToken)
    {
        const int batchSize = 100;
        var accountIds = group.Select(p => p.AccountId).Distinct().ToList();

        for (int offset = 0; offset < accountIds.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = accountIds.Skip(offset).Take(batchSize).ToList();
            string idList = string.Join("%2C", batch);
            const string fields = "hidden_profile%2Cstatistics.pvp.wins%2Cstatistics.pvp.battles"
                + "%2Cstatistics.pvp.damage_dealt"
                + "%2Cstatistics.pvp_solo.wins%2Cstatistics.pvp_solo.battles"
                + "%2Cstatistics.pvp_div2.wins%2Cstatistics.pvp_div2.battles"
                + "%2Cstatistics.pvp_div3.wins%2Cstatistics.pvp_div3.battles";
            const string extra = "statistics.pvp_solo%2Cstatistics.pvp_div2%2Cstatistics.pvp_div3";

            string url = $"https://{host}/wows/account/info/"
                + $"?application_id={options.ApplicationId}&extra={extra}&fields={fields}&account_id={idList}";

            string json;
            try
            {
                json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单批失败不影响其他批。
                continue;
            }

            var statsById = WgApiParser.ParseAccountStats(json);
            ApplyStats(group, statsById);

            if (offset + batchSize < accountIds.Count)
            {
                await Task.Delay(options.DelayBetweenRequests, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 把拉到的统计写回玩家记录。
    /// </summary>
    private static void ApplyStats(
        List<PlayerRecord> group,
        Dictionary<string, WgApiParser.AccountStats> statsById)
    {
        foreach (var player in group)
        {
            if (!statsById.TryGetValue(player.AccountId, out var stats))
            {
                continue;
            }

            if (stats.IsHidden)
            {
                player.IsHidden = true;
                continue;
            }

            player.AccountBattles = stats.Battles;
            player.AccountWinrate = stats.Winrate;
            player.AccountAvgDamage = stats.AvgDamage;
            player.AccountBattlesSolo = stats.BattlesSolo;
            player.AccountWinrateSolo = stats.WinrateSolo;
            player.AccountBattlesDiv2 = stats.BattlesDiv2;
            player.AccountWinrateDiv2 = stats.WinrateDiv2;
            player.AccountBattlesDiv3 = stats.BattlesDiv3;
            player.AccountWinrateDiv3 = stats.WinrateDiv3;
            player.WeightedWinrate = WeightedWinrate.Compute(player);
        }
    }

    /// <summary>
    /// 批量拉取玩家的军团标签，写回缺失的记录。
    /// </summary>
    /// <remarks>
    /// clans/accountinfo 按 account_id 批量返回，一次最多 100 个账号。标签
    /// 存进 ClanTag 并补方括号，与回放链路的口径一致。军团字段独立于统计，
    /// 隐藏战绩的玩家同样能查到。单批失败只影响这批，跳过继续。
    /// </remarks>
    private async Task FetchPlayerClansAsync(
        List<PlayerRecord> group,
        string host,
        CancellationToken cancellationToken)
    {
        var accountIds = group
            .Select(p => p.AccountId)
            .Where(id => id != "-1" && !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        const int batchSize = 100;
        const string fields = "clan_id%2Cclan.tag";

        for (int offset = 0; offset < accountIds.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = accountIds.Skip(offset).Take(batchSize).ToList();
            string url = $"https://{host}/wows/clans/accountinfo/"
                + $"?application_id={options.ApplicationId}&extra=clan&fields={fields}"
                + $"&account_id={string.Join("%2C", batch)}";

            string json;
            try
            {
                json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单批失败不影响其他批与整体统计。
                continue;
            }

            var tagsById = WgApiParser.ParsePlayerClans(json);
            foreach (var player in group)
            {
                if (string.IsNullOrEmpty(player.ClanTag)
                    && tagsById.TryGetValue(player.AccountId, out string? tag))
                {
                    player.ClanTag = $"[{tag}]";
                }
            }

            if (offset + batchSize < accountIds.Count)
            {
                await Task.Delay(options.DelayBetweenRequests, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 逐玩家拉取单船统计，有界并发推进。
    /// </summary>
    /// <remarks>
    /// 与全量路径同一并发策略。此路径拿不到分舰种聚合，仅在关闭
    /// FetchClassStats 时使用。
    /// </remarks>
    private async Task FetchShipStatsAsync(
        List<PlayerRecord> group,
        string host,
        CancellationToken cancellationToken)
    {
        // original_exp 是账号 statistics 端点的字段，ships/stats 不认，
        // 带上会让整个请求报 INVALID_FIELDS。
        const string fields = "pvp.wins%2Cpvp.battles%2Cpvp.damage_dealt";
        const int maxConcurrency = 4;

        var targets = group
            .Where(p => !string.IsNullOrEmpty(p.ShipId) && p.ShipId != "-1")
            .Where(p => !string.IsNullOrEmpty(p.AccountId) && p.AccountId != "-1")
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = targets.Select(async player =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string url = $"https://{host}/wows/ships/stats/"
                    + $"?application_id={options.ApplicationId}&fields={fields}"
                    + $"&account_id={player.AccountId}&ship_id={player.ShipId}";

                string json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var (battles, winrate, avgDamage, avgExp) =
                    WgApiParser.ParseShipStats(json, player.AccountId);

                player.ShipBattles = battles;
                player.ShipWinrate = winrate;
                player.ShipAvgDamage = avgDamage;
                player.ShipAvgExp = avgExp;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 单船查询失败不影响其他玩家。
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 确保 ship_id 到舰种的映射就绪。缓存 24 小时，全量拉取按页翻页。
    /// </summary>
    /// <remarks>
    /// encyclopedia/ships 每页 100 条，全目录约 6 页，缓存期内零请求。
    /// 加载失败时保留空映射，分舰种聚合退化为空，不影响账号级统计。
    /// </remarks>
    private async Task EnsureShipClassLookupAsync(string host, CancellationToken cancellationToken)
    {
        if (shipClassLookup.Count > 0
            && DateTimeOffset.UtcNow - shipClassLookupLoadedAt < options.ClassCatalogCacheDuration)
        {
            return;
        }

        // 失败负缓存：目录不可达（断网、限流）时连续重试只会把 6 页超时
        // 串进每次补拉。负缓存期内直接放弃，分舰种聚合退化为空，账号级
        // 统计不受影响。缓存命中成功或负缓存期过后重试。
        if (DateTimeOffset.UtcNow < shipClassLookupFailedUntil)
        {
            return;
        }

        try
        {
            // 页数从 encyclopedia/ships 端点自己的 meta.page_total 取，
            // encyclopedia/info 没有页数字段，旧实现先请求 info 的路径
            // 字段写错被 API 拒绝，catch 吞掉后目录永远为空。
            var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
            var tierLookup = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int page = 1; ; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string url = $"https://{host}/wows/encyclopedia/ships/"
                    + $"?application_id={options.ApplicationId}&fields=type%2Ctier&page_no={page}";
                string json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                WgApiParser.ParseShipClasses(json, lookup, tierLookup);

                int pageTotal = WgApiParser.ParseShipsPageTotal(json);
                if (page >= pageTotal)
                {
                    break;
                }

                await Task.Delay(options.DelayBetweenRequests, cancellationToken).ConfigureAwait(false);
            }

            if (lookup.Count > 0)
            {
                shipClassLookup = lookup;
                shipTierLookup = tierLookup;
                shipClassLookupLoadedAt = DateTimeOffset.UtcNow;
                shipClassLookupFailedUntil = DateTimeOffset.MinValue;
            }
            else
            {
                shipClassLookupFailedUntil = DateTimeOffset.UtcNow + ClassLookupFailureBackoff;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 目录加载失败不影响账号级统计，分舰种特征退化为未知。
            shipClassLookupFailedUntil = DateTimeOffset.UtcNow + ClassLookupFailureBackoff;
        }
    }

    /// <summary>
    /// 逐玩家拉取全量船只统计，有界并发推进。
    /// </summary>
    /// <remarks>
    /// 一次请求返回该玩家全部船只，替代原逐玩家单船查询。WG 的 ships/stats
    /// 端点一次只认一个 account_id，协议上无法按账号批量，改为有界并发：
    /// 同时在飞的请求数封顶，请求间不再串行等待，24 人整队从串行的十几秒
    /// 压到两三秒。并发度取 4，兼顾提速与限流：WG 公开 API 的配额按应用
    /// 聚合计，4 路并发下 429 由 WgHttp 的退避重试兜底。
    /// 舰种映射来自 encyclopedia 目录缓存，缺失映射的船只不计入聚合。
    /// </remarks>
    private async Task FetchAllShipStatsAsync(
        List<PlayerRecord> group,
        string host,
        CancellationToken cancellationToken)
    {
        // original_exp 是账号 statistics 端点的字段，ships/stats 不认，
        // 带上会让整个请求报 INVALID_FIELDS。
        const string fields = "ship_id%2Cpvp.wins%2Cpvp.battles%2Cpvp.damage_dealt";
        const int maxConcurrency = 4;

        var targets = group
            .Where(p => !string.IsNullOrEmpty(p.AccountId) && p.AccountId != "-1")
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = targets.Select(async player =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string url = $"https://{host}/wows/ships/stats/"
                    + $"?application_id={options.ApplicationId}&fields={fields}"
                    + $"&account_id={player.AccountId}";

                string json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var allShips = WgApiParser.ParseAllShipStats(json);
                if (allShips.TryGetValue(player.AccountId, out var byShip))
                {
                    ApplyShipStats(player, byShip);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 单船查询失败不影响其他玩家。
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 把全量船只统计写回玩家记录：单船数据取本局所开的船，分舰种聚合成
    /// JSON 存入 ClassStatsJson，账号最常玩等级取场次最多的等级。
    /// </summary>
    private void ApplyShipStats(
        PlayerRecord player,
        Dictionary<string, WgApiParser.ShipStatsEntry> byShip)
    {
        var classAgg = new Dictionary<string, (double Battles, double Wins, double Damage)>(StringComparer.Ordinal);

        // 账号层：等级到场次的直方图，最常玩等级取峰值。
        var tierHistogram = new Dictionary<int, double>();

        foreach (var (shipId, entry) in byShip)
        {
            // 本局所开的船，填充单船统计。
            if (player.ShipId == shipId)
            {
                player.ShipBattles = entry.Battles;
                player.ShipWinrate = entry.Battles > 0 ? entry.Wins / entry.Battles : -1;
                player.ShipAvgDamage = entry.Battles > 0 ? entry.DamageDealt / entry.Battles : -1;
            }

            // 账号常用等级直方图，等级目录缺失的船跳过。
            if (shipTierLookup.TryGetValue(shipId, out int tier))
            {
                tierHistogram[tier] = tierHistogram.TryGetValue(tier, out var cur) ? cur + entry.Battles : entry.Battles;
            }

            string? classKey = ResolveClassKey(shipId);
            if (classKey is null)
            {
                continue;
            }

            var (battles, wins, damage) = classAgg.TryGetValue(classKey, out var cur2)
                ? cur2
                : (0, 0, 0);
            classAgg[classKey] = (battles + entry.Battles, wins + entry.Wins, damage + entry.DamageDealt);
        }

        // 保持稳定顺序，便于快照 JSON 可比对。伤害维度供舰种熟练列展示场均。
        var parts = classAgg
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var (b, w, d) = kv.Value;
                return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"\"{kv.Key}\":{{\"battles\":{b:0},\"wins\":{w:0},\"damage\":{d:0}}}");
            });

        player.ClassStatsJson = "{" + string.Join(",", parts) + "}";

        // 最常玩等级：场次最多的等级。并列取高等级（现版本环境更有代表性）。
        double maxBattles = 0;
        int bestTier = -1;
        foreach (var (tier, battles) in tierHistogram)
        {
            if (battles > maxBattles || (battles == maxBattles && tier > bestTier))
            {
                maxBattles = battles;
                bestTier = tier;
            }
        }

        player.FavoriteTier = bestTier;
    }

    /// <summary>
    /// 把 ship_id 解析成稳定舰种键，形如 "BB"。查不到返回 null。
    /// </summary>
    private string? ResolveClassKey(string shipId)
    {
        if (shipClassLookup.Count == 0)
        {
            return null;
        }

        return shipClassLookup.TryGetValue(shipId, out string? wgType) ? ToClassKey(wgType) : null;
    }

    /// <summary>
    /// WG 舰种字符串转稳定存储键，与 <see cref="ShipClassExtensions.Parse"/> 的输入兼容。
    /// </summary>
    /// <remarks>
    /// 映射表本身只养在 <see cref="ShipClassExtensions"/> 一处，这里只做适配。
    /// 有意收窄输入面：encyclopedia/ships 返回的是 Destroyer、AirCarrier 这类
    /// 完整类型名，不认 DD/CA/CL 这类缩写。若直接放行缩写，“CL”会被当年的
    /// 巡洋并入 CA 键，分舰种聚合就多算了一类。
    /// </remarks>
    internal static string? ToClassKey(string wgType)
    {
        string normalized = wgType.Trim().ToUpperInvariant();
        bool isKnownWgType = normalized is
            "DESTROYER" or "CRUISER" or "BATTLESHIP"
            or "AIR_CARRIER" or "AIRCARRIER" or "CARRIER"
            or "SUBMARINE";
        if (!isKnownWgType)
        {
            return null;
        }

        ShipClass shipClass = ShipClassExtensions.Parse(wgType);
        return shipClass == ShipClass.Unknown ? null : shipClass.ToStorageKey();
    }

    private Task<string> GetStringAsync(string url, CancellationToken cancellationToken) =>
        WgHttp.GetStringAsync(httpClient, url, options.RateLimitBackoff, cancellationToken);
}

/// <summary>
/// WG 公开 API 配置。
/// </summary>
public sealed class WgPublicOptions
{
    /// <summary>
    /// 应用 ID。默认值来自 ApeRadar 公开分享的 ID。
    /// 生产环境建议自带 ID 以免受他人配额影响。
    /// </summary>
    public string ApplicationId { get; set; } = "447ec579e994976e39dec0e7d0bac644";

    /// <summary>请求之间的延迟，避免触发限流。</summary>
    public TimeSpan DelayBetweenRequests { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>遇到限流时的退避时间。</summary>
    public TimeSpan RateLimitBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 跳过单船统计查询。
    /// </summary>
    /// <remarks>
    /// 单船统计是逐玩家调用，是三步里最慢的一步。批量回测只关心账号胜率时，
    /// 开启此项能把请求数从每玩家 1 次降到每批 1 次。
    /// </remarks>
    public bool SkipShipStats { get; set; }

    /// <summary>
    /// 拉取分舰种聚合统计。
    /// </summary>
    /// <remarks>
    /// 开启后用全量船只查询替代逐玩家单船查询，请求数不变，但响应体更大。
    /// 首次启用会额外请求 encyclopedia 目录并缓存 24 小时。
    /// </remarks>
    public bool FetchClassStats { get; set; }

    /// <summary>舰船目录缓存的时长。</summary>
    public TimeSpan ClassCatalogCacheDuration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// 拉取玩家军团标签。
    /// </summary>
    /// <remarks>
    /// 回放链路自带回放里的标签，赛前 tempArenaInfo 不含标签，需要按账号补查。
    /// clans/accountinfo 按账号批量返回，一批一次请求，代价小，默认开启。
    /// </remarks>
    public bool FetchClanTags { get; set; } = true;

    /// <summary>校验必要配置。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApplicationId))
        {
            throw new ArgumentException("必须指定 WG API 应用 ID。", nameof(ApplicationId));
        }
    }
}
