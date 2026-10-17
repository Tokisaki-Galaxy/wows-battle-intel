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
/// FileSystemWatcher 会触发多次且可能在写入完成前触发。轮询配合文件
/// 稳定判定更可靠，且游戏回放目录不深，扫描代价低。
///
/// 首次扫描只登记不通知的老做法会让应用没在运行期间打完的每一局永久漏掉。
/// 现在首次扫描逐份比对库：已入库的记为已知，未入库的交给
/// <see cref="ReplayDiscovered"/>，由消费方决定是否收集。
/// </remarks>
public sealed class ReplayObserver : IDisposable
{
    /// <summary>
    /// 判断某份回放是否已入库的委托。返回 false 表示这是漏掉的场次。
    /// </summary>
    public delegate Task<bool> KnownReplayCheck(string replayPath, DateTimeOffset startedAt);

    private readonly AppConfig config;
    private readonly KnownReplayCheck? knownCheck;
    private readonly bool reportOnFirstScan;
    private readonly HashSet<string> knownFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object stateLock = new();
    private CancellationTokenSource? cts;
    private Task? loop;

    /// <summary>
    /// 发现新回放时触发。参数为回放文件路径。
    /// </summary>
    public event Action<string>? ReplayDiscovered;

    /// <summary>
    /// 构造观测器。
    /// </summary>
    /// <param name="config">配置。</param>
    /// <param name="knownCheck">
    /// 首次扫描时用来判断回放是否已入库。为空时首次扫描不做比对。
    /// </param>
    /// <param name="reportOnFirstScan">
    /// 首次扫描是否上报未入库的回放。启动时开启，补上应用没运行时打完的
    /// 场次；设置页换绑配置时关闭，避免扫描目录的全部历史回放被重新入队。
    /// </param>
    public ReplayObserver(
        AppConfig config,
        KnownReplayCheck? knownCheck = null,
        bool reportOnFirstScan = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        this.config = config;
        this.knownCheck = knownCheck;
        this.reportOnFirstScan = reportOnFirstScan;
    }

    /// <summary>
    /// 启动观测。
    /// </summary>
    public void Start()
    {
        lock (stateLock)
        {
            if (loop is not null)
            {
                return;
            }

            cts = new CancellationTokenSource();
            loop = Task.Run(() => ObserveLoopAsync(cts.Token));
        }
    }

    /// <summary>
    /// 停止观测。失败时等不到退出就不释放取消源，避免后台任务拿到已释放的
    /// 令牌源。
    /// </summary>
    public void Stop()
    {
        Task? running;
        CancellationTokenSource? source;
        lock (stateLock)
        {
            running = loop;
            source = cts;
            loop = null;
            cts = null;
        }

        source?.Cancel();
        bool stopped = true;
        try
        {
            stopped = running?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        }
        catch
        {
            // 停止超时不阻断退出。
        }

        if (stopped)
        {
            source?.Dispose();
        }
    }

    /// <summary>
    /// 轮询循环。
    /// </summary>
    private async Task ObserveLoopAsync(CancellationToken cancellationToken)
    {
        await FirstScanAsync(cancellationToken).ConfigureAwait(false);

        var interval = TimeSpan.FromSeconds(Math.Max(3, config.PollIntervalSeconds));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                ScanOnce();
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
    /// 首次扫描：库里已有的直接登记为已知，未入库的上报给消费方。
    /// </summary>
    /// <remarks>
    /// 不设空闲门槛：刚打完一局就启动应用时，文件可能才存在几秒，等 5 秒
    /// 会让这一局被登记后彻底跳过。
    ///
    /// 全程兜底：比对要查库，库被占用或文件被删都可能抛异常，异常一旦
    /// 冒泡到上层，整个观测循环就静默退出了，这比漏一局更糟。
    /// </remarks>
    private async Task FirstScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            var files = ReplayDirectoryScanner.Scan(config.ReplayDirectory, TimeSpan.Zero, cancellationToken);
            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                bool known = knownCheck is not null
                    && await knownCheck(file.Path, file.StartedAt).ConfigureAwait(false);

                MarkKnown(file.Path);
                if (!known && reportOnFirstScan)
                {
                    ReplayDiscovered?.Invoke(file.Path);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止观测。
        }
        catch
        {
            // 首扫失败不影响后续轮询，下一轮照常扫描。
        }
    }

    /// <summary>
    /// 扫描一次回放目录。
    /// </summary>
    private void ScanOnce()
    {
        var files = ReplayDirectoryScanner.Scan(
            config.ReplayDirectory,
            TimeSpan.FromSeconds(5));

        foreach (var file in files)
        {
            if (MarkKnown(file.Path))
            {
                ReplayDiscovered?.Invoke(file.Path);
            }
        }
    }

    /// <summary>登记已知路径。返回是否为首次见到。</summary>
    private bool MarkKnown(string path)
    {
        lock (knownFiles)
        {
            return knownFiles.Add(path);
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
