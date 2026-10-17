using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Stats;

/// <summary>
/// 军团强度目录。按军团标签批量查询分档，供名单着色与 ToolTip 使用。
/// </summary>
/// <remarks>
/// 查询前先查本地缓存，只对 miss 的标签走网络，结果回写缓存。
/// 网络侧三步拉取：clans/list 按标签精确匹配军团，clans/info 拿成员列表，
/// account/info 批量取成员统计后按 ClanGrading 分档。
/// 进程内缓存叠加在持久缓存之上，容量超限整表清空，简单够用不做 LRU。
/// 任何一步失败该标签回落 Normal，静默处理不阻断名单加载。
/// </remarks>
public sealed class ClanCatalogService
{
    private const int MaxCachedEntries = 500;

    private readonly HttpClient httpClient;
    private readonly WgPublicOptions options;
    private readonly IClanGradeCache? gradeCache;
    private readonly object cacheLock = new();
    private Dictionary<string, ClanGradeInfo> cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 构造服务。缓存仓储可选，缺省时退回纯内存缓存。
    /// </summary>
    public ClanCatalogService(
        WgPublicOptions options,
        IClanGradeCache? gradeCache = null,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        this.gradeCache = gradeCache;
        this.httpClient = httpClient ?? WgHttp.CreateClient(TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// 批量查询军团分档，返回标签到分档信息的映射。
    /// </summary>
    /// <param name="clanTags">军团标签，允许带方括号，自动去重。</param>
    /// <param name="server">服务器，决定 API 域名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyDictionary<string, ClanGradeInfo>> GetGradesAsync(
        IEnumerable<string> clanTags,
        GameServer server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clanTags);

        var result = new Dictionary<string, ClanGradeInfo>(StringComparer.Ordinal);
        if (server == GameServer.Auto)
        {
            return result;
        }

        var pending = new List<string>();
        lock (cacheLock)
        {
            foreach (string rawTag in clanTags)
            {
                string tag = NormalizeTag(rawTag);
                if (tag.Length == 0)
                {
                    continue;
                }

                if (cache.TryGetValue(tag, out var cached))
                {
                    result[tag] = cached;
                }
                else if (!pending.Contains(tag, StringComparer.Ordinal))
                {
                    pending.Add(tag);
                }
            }
        }

        // 进程内 miss 的标签再查持久缓存，命中的回填内存层并计入结果。
        // 持久缓存读取失败视为无缓存，走网络兜底。
        if (pending.Count > 0 && gradeCache is not null)
        {
            try
            {
                var persisted = await gradeCache
                    .GetClanGradesAsync(pending, cancellationToken)
                    .ConfigureAwait(false);
                if (persisted.Count > 0)
                {
                    lock (cacheLock)
                    {
                        foreach (var (tag, info) in persisted)
                        {
                            cache[tag] = info;
                            result[tag] = info;
                        }
                    }

                    pending.RemoveAll(result.ContainsKey);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 缓存读取失败不阻断查询，剩余标签继续走网络。
            }
        }

        if (pending.Count == 0)
        {
            return result;
        }

        string host = WgApiParser.GetApiHost(server);
        var fetched = new List<ClanGradeInfo>();
        foreach (string tag in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ClanGradeInfo info;
            try
            {
                info = await FetchClanAsync(tag, host, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单个军团查询失败不影响其他军团，不缓存失败结果，
                // 下次同标签重试。
                continue;
            }

            lock (cacheLock)
            {
                if (cache.Count >= MaxCachedEntries)
                {
                    cache = new Dictionary<string, ClanGradeInfo>(StringComparer.Ordinal);
                }

                cache[tag] = info;
            }

            result[tag] = info;
            fetched.Add(info);
        }

        // 网络拉到的结果回写持久缓存。写入失败只影响下次命中，静默即可。
        if (fetched.Count > 0 && gradeCache is not null)
        {
            try
            {
                await gradeCache
                    .SaveClanGradesAsync(fetched, DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        return result;
    }

    /// <summary>
    /// 去掉标签两侧的方括号并 trim。公开供视图模型层清洗回放标签。
    /// </summary>
    public static string NormalizeTag(string rawTag)
    {
        string tag = rawTag.Trim();
        if (tag.Length >= 2 && tag.StartsWith("[") && tag.EndsWith("]"))
        {
            tag = tag[1..^1];
        }

        return tag.Trim();
    }

    /// <summary>
    /// 拉取单个军团的完整分档信息。
    /// </summary>
    private async Task<ClanGradeInfo> FetchClanAsync(string tag, string host, CancellationToken cancellationToken)
    {
        // 步骤一：按标签搜索，type=exact 精确匹配避免前缀命中。
        string listUrl = $"https://{host}/wows/clans/list/"
            + $"?application_id={options.ApplicationId}&type=exact&search={Uri.EscapeDataString(tag)}";
        string listJson = await GetStringAsync(listUrl, cancellationToken).ConfigureAwait(false);
        var matches = WgApiParser.ParseClanList(listJson);

        // 同名军团取成员最多的那个，活跃度更高。
        var clan = matches
            .OrderByDescending(c => c.MembersCount)
            .FirstOrDefault(c => string.Equals(c.Tag, tag, StringComparison.Ordinal));
        if (clan is null || clan.MembersCount <= 0)
        {
            return new ClanGradeInfo { Tag = tag, AggregateWinrate = -1 };
        }

        // 步骤二：军团详情与成员列表。
        string infoUrl = $"https://{host}/wows/clans/info/"
            + $"?application_id={options.ApplicationId}&clan_id={clan.ClanId}"
            + "&fields=tag%2Cname%2Cmembers_count%2Cmembers_ids";
        string infoJson = await GetStringAsync(infoUrl, cancellationToken).ConfigureAwait(false);
        var infoById = WgApiParser.ParseClanInfo(infoJson);
        if (!infoById.TryGetValue(clan.ClanId, out var detail) || detail.MemberIds.Count == 0)
        {
            return new ClanGradeInfo
            {
                Tag = tag,
                Name = clan.Name,
                MembersCount = clan.MembersCount,
                AggregateWinrate = -1,
            };
        }

        // 步骤三：成员账号统计批量聚合，单批 100 个账号。
        double totalWins = 0;
        double totalBattles = 0;
        int visibleMembers = 0;
        const int batchSize = 100;
        const string fields = "hidden_profile%2Cstatistics.pvp.wins%2Cstatistics.pvp.battles";

        for (int offset = 0; offset < detail.MemberIds.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = detail.MemberIds.Skip(offset).Take(batchSize).ToList();
            string url = $"https://{host}/wows/account/info/"
                + $"?application_id={options.ApplicationId}&fields={fields}"
                + $"&account_id={string.Join("%2C", batch)}";

            string json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var statsById = WgApiParser.ParseAccountStats(json);
            foreach (var memberId in batch)
            {
                if (!statsById.TryGetValue(memberId, out var stats)
                    || stats.IsHidden
                    || stats.Battles < 0)
                {
                    continue;
                }

                visibleMembers++;
                totalWins += Math.Max(0, stats.Winrate >= 0 ? stats.Winrate * stats.Battles : 0);
                totalBattles += Math.Max(0, stats.Battles);
            }

            if (offset + batchSize < detail.MemberIds.Count)
            {
                await Task.Delay(options.DelayBetweenRequests, cancellationToken).ConfigureAwait(false);
            }
        }

        double winrate = totalBattles > 0 ? totalWins / totalBattles : -1;
        return new ClanGradeInfo
        {
            Tag = tag,
            Name = detail.Name,
            MembersCount = detail.MembersCount,
            VisibleMembers = visibleMembers,
            AggregateWinrate = winrate,
            Grade = ClanGrading.Grade(winrate, visibleMembers, totalBattles),
        };
    }

    private Task<string> GetStringAsync(string url, CancellationToken cancellationToken) =>
        WgHttp.GetStringAsync(httpClient, url, options.RateLimitBackoff, cancellationToken);
}
