using System.IO;
using System.Net.Http;
using System.Web;
using System.Windows;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using WowsBattleIntel.Client.Configuration;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 客户端自动更新，基于 Velopack，更新源为 GitHub Releases。
/// </summary>
/// <remarks>
/// 流程见 docs/decisions/0007-auto-update.md：启动延迟检查、六小时节流、
/// 后台下载、用户确认后原子换文件重启。所有失败静默降级，不影响正常使用。
///
/// 版本发现不走 GitHub API：直接请求 releases/latest 页面，跟随重定向拿到
/// 最新 tag，规避未认证 API 60 次/时的限额。feed 与资产从该 tag 的下载
/// 直链取，同走 CDN，不受限额约束。
/// </remarks>
public static class UpdateService
{
    /// <summary>更新检查最小间隔。页面请求虽无限额，节流保持礼貌。</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>启动后延迟检查，避开初始化竞态。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    /// <summary>仓库 releases 页根地址。</summary>
    private const string RepoUrl = "https://github.com/tokisaki-galaxy/wows-battle-intel";

    private static readonly Lock gate = new();
    private static DateTimeOffset lastCheck = DateTimeOffset.MinValue;

    /// <summary>节流时间戳落盘路径。</summary>
    private static string StampPath => AppPaths.CombineAppData("update-check.stamp");

    /// <summary>
    /// 启动时调用：延迟后台检查，有新版提示并按用户意愿应用。不抛异常。
    /// </summary>
    public static void CheckOnStartup(AppConfig config)
    {
        if (!config.AutoUpdate)
        {
            Logs?.Info("自动更新已关闭，跳过检查");
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(StartupDelay).ConfigureAwait(false);
            await CheckAndPromptAsync(config).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// 执行一次检查。节流通过，返回是否有可用更新；异常静默记日志。
    /// </summary>
    public static async Task<bool> CheckAndPromptAsync(AppConfig config)
    {
        lock (gate)
        {
            if (DateTimeOffset.UtcNow - lastCheck < CheckInterval)
            {
                Logs?.Info("自动更新：距上次检查不足六小时，跳过");
                return false;
            }

            lastCheck = DateTimeOffset.UtcNow;
        }

        try
        {
            var tag = await ResolveLatestTagAsync(config).ConfigureAwait(false);
            if (tag is null)
            {
                Logs?.Info("自动更新：未能解析最新版本，跳过");
                return false;
            }

            // feed 放在该 tag 的资产目录，SimpleWebSource 从 {base}/RELEASES 取清单。
            var feedBase = new Uri($"{RepoUrl}/releases/download/{tag}/");
            var source = new SimpleWebSource(feedBase, new AuthedFileDownloader(config.GithubToken));
            var mgr = new UpdateManager(source);

            if (!mgr.IsInstalled)
            {
                // 开发环境或未按 Velopack 布局部署的目录，无法自更新，跳过。
                Logs?.Info("自动更新：当前以非打包形态运行，跳过");
                return false;
            }

            var update = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                Logs?.Info("自动更新：已是最新版本");
                WriteStamp();
                return false;
            }

            Logs?.Info($"自动更新：发现新版本 {update.TargetFullRelease.Version}");

            await mgr.DownloadUpdatesAsync(
                update,
                p => Logs?.Info($"自动更新：下载进度 {p:P0}"))
                .ConfigureAwait(false);

            // 用户确认后应用。用消息框同步确认，拒绝则更新包保留，下次启动再提示。
            var apply = Application.Current?.Dispatcher.Invoke(() => MessageBox.Show(
                $"发现新版本 {update.TargetFullRelease.Version}，已下载完成。\n\n是否立即安装并重启？",
                "wows-battle-intel",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question));

            WriteStamp();
            if (apply == MessageBoxResult.OK)
            {
                Logs?.Info("自动更新：应用更新并重启");
                mgr.ApplyUpdatesAndRestart(update);
            }
            else
            {
                Logs?.Info("自动更新：用户推迟，更新包已就绪，下次启动再提示");
            }

            return true;
        }
        catch (Exception ex)
        {
            Logs?.Warn($"自动更新检查失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 解析最新版本 tag。
    ///
    /// 请求 releases/latest，GitHub 302 到 /releases/tag/vX.Y.Z，从最终地址
    /// 提取 tag。全程无 API 调用，无限额。仓库 private 期间需带 token 访问。
    /// </summary>
    private static async Task<string?> ResolveLatestTagAsync(AppConfig config)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{RepoUrl}/releases/latest");
        if (!string.IsNullOrEmpty(config.GithubToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", config.GithubToken);
        }

        using var response = await UpdateServiceHttp.Client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var finalUrl = response.RequestMessage?.RequestUri?.ToString();
        if (string.IsNullOrEmpty(finalUrl))
        {
            return null;
        }

        // 形如 {RepoUrl}/releases/tag/v0.2.0，取 tag 段。
        var segments = new Uri(finalUrl).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var tagIndex = Array.LastIndexOf(segments, "tag");
        if (tagIndex < 0 || tagIndex == segments.Length - 1)
        {
            return null;
        }

        return HttpUtility.UrlDecode(segments[tagIndex + 1]);
    }

    /// <summary>写节流时间戳，供诊断。</summary>
    private static void WriteStamp()
    {
        try
        {
            File.WriteAllText(StampPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (IOException)
        {
            // 时间戳写失败不影响更新流程。
        }
    }

    /// <summary>应用级运行日志。服务装配完成前为 null，静默跳过。</summary>
    private static LogService? Logs => App.Services?.Logs;
}

/// <summary>
/// 带可选鉴权的文件下载器，供 SimpleWebSource 拉 feed 与 nupkg。
///
/// token 为空时行为与默认下载器一致，仓库转 public 后无需任何配置。
/// </summary>
internal sealed class AuthedFileDownloader : IFileDownloader
{
    private readonly string? token;

    public AuthedFileDownloader(string? token) => this.token = token;

    /// <inheritdoc />
    public async Task DownloadFile(
        string url,
        string targetFile,
        Action<int>? progress = null,
        IDictionary<string, string>? headers = null,
        double timeout = 30,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, url, headers, cancellationToken)
            .ConfigureAwait(false);
        var total = response.Content.Headers.ContentLength ?? -1;

        var dir = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(targetFile);

        var buffer = new byte[81920];
        long written = 0;
        int lastPercent = -1;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            if (progress is not null && total > 0)
            {
                var percent = (int)(written * 100 / total);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress(percent);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> DownloadBytes(
        string url,
        IDictionary<string, string>? headers = null,
        double timeout = 30)
    {
        using var response = await SendAsync(HttpMethod.Get, url, headers).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> DownloadString(
        string url,
        IDictionary<string, string>? headers = null,
        double timeout = 30)
    {
        using var response = await SendAsync(HttpMethod.Get, url, headers).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        IDictionary<string, string>? headers,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", token);
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var response = await UpdateServiceHttp.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response;
    }
}

/// <summary>更新服务与下载器共享的 HttpClient，长超时兼顾大包下载。</summary>
internal static class UpdateServiceHttp
{
    /// <summary>进程内唯一的共享客户端，避免重复构造 handler 耗尽连接池。</summary>
    internal static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("wows-battle-intel-updater");
        return client;
    }
}

/// <summary>Velopack 日志转发到运行日志页签，在 App 启动钩子里经 SetLogger 挂载。</summary>
internal sealed class VelopackLogAdapter : IVelopackLogger
{
    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
    {
        var text = exception is null ? message : $"{message}: {exception.Message}";
        var logs = App.Services?.Logs;
        switch (logLevel)
        {
            case VelopackLogLevel.Error:
            case VelopackLogLevel.Critical:
                logs?.Error($"[vpk] {text}");
                break;
            case VelopackLogLevel.Warning:
                logs?.Warn($"[vpk] {text}");
                break;
            default:
                logs?.Info($"[vpk] {text}");
                break;
        }
    }
}
