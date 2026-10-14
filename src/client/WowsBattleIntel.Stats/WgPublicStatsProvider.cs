using System.Net;
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

    /// <summary>
    /// 构造提供者。
    /// </summary>
    public WgPublicStatsProvider(WgPublicOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        this.httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
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

        // 按服务器分组，不同服的 API 域名不同。
        foreach (var group in targets.GroupBy(p => p.Server))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Key == GameServer.Auto)
            {
                continue;
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

        // --- 步骤二：账号统计 ---
        await FetchAccountStatsAsync(resolved, host, cancellationToken).ConfigureAwait(false);

        // --- 步骤三：单船统计，逐玩家。可按需跳过以提速。 ---
        if (!options.SkipShipStats)
        {
            await FetchShipStatsAsync(resolved, host, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 用昵称批量换账号 ID。
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveAccountIdsAsync(
        List<PlayerRecord> group,
        string host,
        CancellationToken cancellationToken)
    {
        // 昵称去重后拼接，WG 支持逗号分隔的精确查询。
        var names = group
            .Select(p => p.PlayerName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        string nameList = Uri.EscapeDataString(string.Join(",", names));
        string url = $"https://{host}/wows/account/list/"
            + $"?application_id={options.ApplicationId}&type=exact&search={nameList}";

        string json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var mapping = WgApiParser.ParseAccountIds(json);

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
    /// 逐玩家拉取单船统计。
    /// </summary>
    private async Task FetchShipStatsAsync(
        List<PlayerRecord> group,
        string host,
        CancellationToken cancellationToken)
    {
        const string fields = "pvp.wins%2Cpvp.battles%2Cpvp.damage_dealt%2Cpvp.original_exp";

        foreach (var player in group)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(player.ShipId) || player.ShipId == "-1")
            {
                continue;
            }

            string url = $"https://{host}/wows/ships/stats/"
                + $"?application_id={options.ApplicationId}&fields={fields}"
                + $"&account_id={player.AccountId}&ship_id={player.ShipId}";

            try
            {
                string json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var (battles, winrate, avgDamage, avgExp) =
                    WgApiParser.ParseShipStats(json, player.AccountId);

                player.ShipBattles = battles;
                player.ShipWinrate = winrate;
                player.ShipAvgDamage = avgDamage;
                player.ShipAvgExp = avgExp;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单船查询失败不影响其他玩家。
            }

            await Task.Delay(options.DelayBetweenRequests, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // 限流时退避后重试一次。
            await Task.Delay(options.RateLimitBackoff, cancellationToken).ConfigureAwait(false);
            using var retry = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            retry.EnsureSuccessStatusCode();
            return await retry.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
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

    /// <summary>校验必要配置。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApplicationId))
        {
            throw new ArgumentException("必须指定 WG API 应用 ID。", nameof(ApplicationId));
        }
    }
}
