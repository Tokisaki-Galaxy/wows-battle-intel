using System.IO;
using WowsBattleIntel.Client.Configuration;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 回放目录观测服务。
/// </summary>
/// <remarks>
/// 定时扫描游戏回放目录，发现新文件时触发事件。
/// 观测与解析严格分离：本服务只负责发现，不解析。
///
/// 用轮询而非 FileSystemWatcher，原因：游戏写回放时可能先建文件再写入，
/// FileSystemWatcher 会触发多次且可能在写入完成前触发。轮询配合文件稳定
/// 判定更可靠，且游戏回放目录不深，扫描代价低。
/// </remarks>
public sealed class ReplayObserver : IDisposable
{
    private readonly AppConfig config;
    private readonly HashSet<string> knownFiles = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cts;
    private Task? loop;

    /// <summary>
    /// 发现新回放时触发。参数为回放文件路径。
    /// </summary>
    public event Action<string>? ReplayDiscovered;

    /// <summary>
    /// 构造观测器。
    /// </summary>
    public ReplayObserver(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        this.config = config;
    }

    /// <summary>
    /// 启动观测。
    /// </summary>
    public void Start()
    {
        if (loop is not null)
        {
            return;
        }

        cts = new CancellationTokenSource();
        loop = Task.Run(() => ObserveLoopAsync(cts.Token));
    }

    /// <summary>
    /// 停止观测。
    /// </summary>
    public void Stop()
    {
        cts?.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 停止超时不阻断退出。
        }

        loop = null;
        cts?.Dispose();
        cts = null;
    }

    /// <summary>
    /// 轮询循环。
    /// </summary>
    private async Task ObserveLoopAsync(CancellationToken cancellationToken)
    {
        // 首次扫描只登记既有文件，不触发事件，避免启动时批量解析历史回放。
        ScanOnce(notify: false);

        var interval = TimeSpan.FromSeconds(Math.Max(3, config.PollIntervalSeconds));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                ScanOnce(notify: true);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 扫描异常不中断循环，下轮重试。
            }
        }
    }

    /// <summary>
    /// 扫描一次回放目录。
    /// </summary>
    /// <param name="notify">是否对新文件触发事件。</param>
    private void ScanOnce(bool notify)
    {
        string replayRoot = config.ReplayDirectory;
        if (!Directory.Exists(replayRoot))
        {
            return;
        }

        // 回放按版本分子目录，递归查找。
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(replayRoot, "*.wowsreplay", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        foreach (string file in files)
        {
            if (Path.GetFileName(file).Equals("temp.wowsreplay", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 文件仍在写入时跳过，下次扫描再处理。
            if (!IsFileStable(file))
            {
                continue;
            }

            if (knownFiles.Add(file) && notify)
            {
                ReplayDiscovered?.Invoke(file);
            }
        }
    }

    /// <summary>
    /// 判断文件是否已写完。以最近修改时间距今超过阈值作为稳定判据。
    /// </summary>
    private static bool IsFileStable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                return false;
            }

            // 回放写完到落盘通常很快，5 秒无变化视为稳定。
            return DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromSeconds(5);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
