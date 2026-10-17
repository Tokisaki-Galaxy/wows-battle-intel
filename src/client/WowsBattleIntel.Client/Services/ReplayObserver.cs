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
/// 现在首次扫描不再交给该集合，而是逐份比对库：已入库的当作已知，未入库的
/// 交给 <see cref="ReplayDiscovered"/>，由消费方决定是否收集。
/// </remarks>
public sealed class ReplayObserver : IDisposable
{
    /// <summary>
    /// 判断某份回放是否已入库的委托。返回 false 表示这是漏掉的场次。
    /// </summary>
    public delegate Task<bool> KnownReplayCheck(string replayPath, DateTimeOffset startedAt);

    private readonly AppConfig config;
    private readonly KnownReplayCheck? knownCheck;
    private readonly HashSet<string> knownFiles = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cts;
    private Task? loop;

    /// <summary>
    /// 发现新回放时触发。参数为回放文件路径。
    /// </summary>
    public event Action<string>? ReplayDiscovered;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="config">配置。</param>
    /// <param name="knownCheck">
    /// 首次扫描时用来判断回放是否已入库。为空时首次扫描行为与后续扫描一致。
    /// </param>
    public ReplayObserver(AppConfig config, KnownReplayCheck? knownCheck = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        this.config = config;
        this.knownCheck = knownCheck;
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
        Task loopTask = Task.Run(() => ObserveLoopAsync(cts.Token));
        loop = loopTask;
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
        await FirstScanAsync(cancellationToken).ConfigureAwait(false);

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
    /// 首次扫描：库里已有的直接登记为已知，未入库的上报给消费方。
    /// </summary>
    /// <remarks>
    /// 不设 5 秒空闲门槛：刚打完一局就启动应用时，文件可能才存在几秒。
    /// 首次扫描的范围有限，读半成品的概率也低。
    /// </remarks>
    private async Task FirstScanAsync(CancellationToken cancellationToken)
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
            if (known)
            {
                // 已入库的也并入已知集合，后续轮询不再重复上报。
                knownFiles.Add(file.Path);
                continue;
            }

            ReplayDiscovered?.Invoke(file.Path);
        }
    }

    /// <summary>
    /// 扫描一次回放目录。
    /// </summary>
    /// <param name="notify">是否对新文件触发事件。</param>
    private void ScanOnce(bool notify)
    {
        var files = ReplayDirectoryScanner.Scan(
            config.ReplayDirectory,
            TimeSpan.FromSeconds(5));

        foreach (var file in files)
        {
            if (knownFiles.Add(file.Path) && notify)
            {
                ReplayDiscovered?.Invoke(file.Path);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
