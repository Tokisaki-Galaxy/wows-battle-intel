namespace WowsBattleIntel.Replay;

/// <summary>
/// replayshark 的 constants.json 本地缓存与按需下载。
/// </summary>
/// <remarks>
/// replayshark 不传 -c 时会自主联网 api.github.com 拉取 constants，国内
/// 网络环境几乎必挂（进程 panic 退出码 101）。因此解析前由本类按回放
/// build 号准备好 constants 文件，再把路径交给 replayshark。
///
/// 数据源是 jsdelivr 对 padtrack/wows-constants 的 CDN 镜像。该仓库按
/// build 号追加文件且历史文件永不修改，所以用 @main 引用不存在内容漂移，
/// 只带来自动跟进新版本。CDN 对分支引用有约 12 小时缓存，新版本上线当天
/// 可能拉不到，此时返回失败原因，由调用方决定稍后重试。
///
/// 版本一致性的锚点是回放头部的 build 号：只精确下载该 build 对应的
/// 文件，不做跨版本兜底。宁可这盘解析失败，也不给预测层喂错版本的数据。
/// </remarks>
public sealed class ConstantsCache
{
    /// <summary>缓存目录里最多保留的版本文件数。</summary>
    public const int MaxCachedVersions = 20;

    /// <summary>jsdelivr 上 constants 仓库的原始文件基址。</summary>
    public const string DefaultMirrorBase =
        "https://cdn.jsdelivr.net/gh/padtrack/wows-constants@main";

    /// <summary>jsdelivr 文件清单 API，用于确认 build 是否已收录。</summary>
    private const string ManifestApi =
        "https://data.jsdelivr.com/v1/packages/gh/padtrack/wows-constants@main?structure=flat";

    private readonly string cacheDirectory;
    private readonly string mirrorBase;
    private readonly HttpClient http;

    // 负缓存：下载失败后短时间内直接判定该 build 不可用，不再发起网络
    // 请求。镜像不可达是持续状态（SSL 阻断、断网），没有负缓存的话每次
    // 解析都要白等一轮 15 秒超时，批量解析时叠加成分钟级卡顿。
    private readonly Dictionary<uint, DateTimeOffset> failedUntil = new();
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 构造缓存。
    /// </summary>
    /// <param name="cacheDirectory">版本文件落盘目录，不存在会自动创建。</param>
    /// <param name="mirrorBase">
    /// 镜像基址，默认 jsdelivr。留空取默认值。
    /// </param>
    /// <param name="handler">测试注入用。为空时新建默认 HttpClient。</param>
    public ConstantsCache(
        string cacheDirectory,
        string? mirrorBase = null,
        HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        this.cacheDirectory = cacheDirectory;
        this.mirrorBase = string.IsNullOrWhiteSpace(mirrorBase)
            ? DefaultMirrorBase
            : mirrorBase.TrimEnd('/');
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>最近一次解析失败的原因，成功时为空。供上层拼错误文案。</summary>
    public string? LastError { get; private set; }

    /// <summary>某 build 的缓存文件路径。文件存在与否不代表已收录。</summary>
    public string CachedPath(uint build) =>
        Path.Combine(cacheDirectory, $"{build}.json");

    /// <summary>
    /// 解析指定 build 的 constants 文件路径，缓存未命中时联网下载。
    /// </summary>
    /// <remarks>
    /// 网络失败不抛异常，返回 null 并把原因写进 <see cref="LastError"/>。
    /// 解析流程据此可以继续走 replayshark 原路，多一次自救机会。
    /// </remarks>
    /// <param name="build">回放头部读出的游戏 build 号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本地 constants.json 路径，失败为 null。</returns>
    public async Task<string?> ResolveAsync(uint build, CancellationToken cancellationToken = default)
    {
        LastError = null;

        var cached = CachedPath(build);
        if (File.Exists(cached))
        {
            return cached;
        }

        // 负缓存期内直接放弃，错误原因沿用上次失败文案，不发网络请求。
        if (failedUntil.TryGetValue(build, out var until) && DateTimeOffset.UtcNow < until)
        {
            LastError = lastFailureReason;
            return null;
        }

        string url = $"{mirrorBase}/data/versions/{build}.json";
        string body;
        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 要区分“数据源还没收录”与“镜像挂了”，后者查一次清单即可。
                if ((int)response.StatusCode == 404)
                {
                    LastError = await ClassifyMissAsync(build, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    LastError = $"constants 镜像返回 HTTP {(int)response.StatusCode}";
                }
                RecordFailure(build);
                return null;
            }

            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = $"constants 镜像不可达: {ex.Message}";
            RecordFailure(build);
            return null;
        }

        // 内容非 JSON 说明镜像返回了错误页，落盘会产生解析期崩溃，先验一手。
        if (!body.TrimStart().StartsWith('{'))
        {
            LastError = "constants 镜像返回了非 JSON 内容";
            RecordFailure(build);
            return null;
        }

        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(cached, body, cancellationToken).ConfigureAwait(false);
        failedUntil.Remove(build);
        Prune();

        return cached;
    }

    /// <summary>最近一次失败的具体原因，负缓存命中时复用。</summary>
    private string? lastFailureReason;

    /// <summary>记录一次失败，进入负缓存期。</summary>
    private void RecordFailure(uint build)
    {
        lastFailureReason = LastError;
        failedUntil[build] = DateTimeOffset.UtcNow + FailureBackoff;
    }

    /// <summary>
    /// 下载返回 404 时判断是数据源未收录还是清单异常。
    /// </summary>
    /// <remarks>
    /// 清单本身不可达时保守地归为镜像问题，提示语区分开，方便用户判断
    /// 该等数据源还是该查网络。
    /// </remarks>
    private async Task<string> ClassifyMissAsync(uint build, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(ManifestApi, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return $"constants 镜像清单返回 HTTP {(int)response.StatusCode}";
            }

            var listing = Newtonsoft.Json.JsonConvert
                .DeserializeObject<JsdelivrListing>(
                    await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            bool published = listing?.Files?.Any(f =>
                string.Equals(f.Name, $"data/versions/{build}.json", StringComparison.Ordinal)) == true;
            return published
                ? $"constants 已收录 build {build} 但镜像尚未同步，请稍后重试"
                : $"constants 数据源尚未收录 build {build}，新版本通常在上线数小时内可用";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"constants 镜像清单不可达: {ex.Message}";
        }
    }

    /// <summary>按文件写入时间保留最新的 20 个版本，超出部分删除。</summary>
    private void Prune()
    {
        // 目录可能混有用户手放的文件，只清理符合命名规则的版本文件。
        var files = Directory
            .EnumerateFiles(cacheDirectory, "*.json")
            .Select(f => new FileInfo(f))
            .Where(f => uint.TryParse(Path.GetFileNameWithoutExtension(f.Name), out _))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        foreach (var stale in files.Skip(MaxCachedVersions))
        {
            try
            {
                stale.Delete();
            }
            catch (IOException)
            {
                // 被并发解析占用的文件跳过，下轮修剪再收。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上，权限异常不阻断解析。
            }
        }
    }

    /// <summary>jsdelivr 清单接口的响应结构，只取需要的字段。</summary>
    private sealed class JsdelivrListing
    {
        /// <summary>仓库内全部文件，名称为相对路径。</summary>
        public JsdelivrFile[]? Files { get; set; }
    }

    private sealed class JsdelivrFile
    {
        /// <summary>相对仓库根的文件路径。</summary>
        public string Name { get; set; } = string.Empty;
    }
}
