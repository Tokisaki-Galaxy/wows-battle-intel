using System.Net;

namespace WowsBattleIntel.Stats;

/// <summary>
/// WG 公开 API 的 HTTP 访问约定。
/// </summary>
/// <remarks>
/// 战绩提供商与军团目录服务面对同一套限流规则，此前各自维护一份
/// 逐字相同的“429 退避后重试一次”实现。收到 429 后先按配置的退避时长
/// 等待再重试一次，重试仍失败则抛出，由调用方按批次降级。
/// </remarks>
internal static class WgHttp
{
    /// <summary>
    /// 创建 HttpClient。超时由调用方按场景决定：批量查询 20 人加分舰种
    /// 聚合是两段串行请求，WG API 高峰期偶发 10 秒级慢响应，战绩侧放宽到
    /// 60 秒；军团目录查询链路短，20 秒足够。
    /// </summary>
    public static HttpClient CreateClient(TimeSpan timeout) => new()
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

        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // 限流时退避后重试一次。
            await Task.Delay(rateLimitBackoff, cancellationToken).ConfigureAwait(false);
            using var retry = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            retry.EnsureSuccessStatusCode();
            return await retry.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
