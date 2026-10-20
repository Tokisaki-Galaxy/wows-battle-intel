using System.Net;

namespace WowsBattleIntel.Stats;

/// <summary>
/// WG 公开 API 的 HTTP 访问约定。
/// </summary>
/// <remarks>
/// 战绩提供商与军团目录服务面对同一套限流规则，此前各自维护一份
/// 逐字相同的“429 退避后重试一次”实现。收到 429 后先按配置的退避时长
/// 等待再重试一次，重试仍失败则抛出，由调用方按批次降级。
///
/// 所有走这套约定的服务共享同一个限流闸门：任一路请求撞上 429，闸门对
/// 全体关停一小段时间，并行中的其余请求在发车前先等闸门放行，不再各自
/// 撞一次限流再各自退避。没有被限流时闸门常开，不引入任何额外延迟。
/// </remarks>
internal static class WgHttp
{
    // 429 后的全体暂停时长。取略长于单次退避，让配额窗口有时间回血。
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromSeconds(3);

    private static readonly object GateLock = new();
    private static DateTimeOffset throttledUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// 创建 HttpClient。超时由调用方按场景决定：批量查询 20 人加分舰种
    /// 聚合是两段串行请求，WG API 高峰期偶发 10 秒级慢响应，战绩侧放宽到
    /// 60 秒；军团目录查询链路短，20 秒足够。
    /// </summary>
    /// <remarks>
    /// 显式配置连接池：连接生命周期限制让 DNS 变化能被感知，避免长跑会话
    /// 一直复用过期连接；并发连接上限与单船统计的并发度对齐，够用且不
    /// 放大突发请求。
    /// </remarks>
    public static HttpClient CreateClient(TimeSpan timeout) => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 8,
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = timeout,
    };

    /// <summary>
    /// GET 一个 URL 并返回响应正文。遇到 429 时退避后重试一次。
    /// </summary>
    /// <param name="httpClient">HTTP 客户端。</param>
    /// <param name="url">请求地址。</param>
    /// <param name="rateLimitBackoff">命中限流后的退避时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<string> GetStringAsync(
        HttpClient httpClient,
        string url,
        TimeSpan rateLimitBackoff,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        await WaitForThrottleWindowAsync(cancellationToken).ConfigureAwait(false);

        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // 限流：先对全体关闸，让并行中的其余请求也停下来，再退避重试一次。
            MarkThrottled();
            await WaitForThrottleWindowAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(rateLimitBackoff, cancellationToken).ConfigureAwait(false);
            using var retry = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (retry.StatusCode == HttpStatusCode.TooManyRequests)
            {
                MarkThrottled();
            }

            retry.EnsureSuccessStatusCode();
            return await retry.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把限流闸门关停一段时间。</summary>
    internal static void MarkThrottled()
    {
        lock (GateLock)
        {
            var until = DateTimeOffset.UtcNow + ThrottleWindow;
            if (until > throttledUntil)
            {
                throttledUntil = until;
            }
        }
    }

    /// <summary>闸门关停期内等待，闸门常开时立即返回。</summary>
    private static async Task WaitForThrottleWindowAsync(CancellationToken cancellationToken)
    {
        TimeSpan remaining;
        lock (GateLock)
        {
            remaining = throttledUntil - DateTimeOffset.UtcNow;
        }

        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }
}
