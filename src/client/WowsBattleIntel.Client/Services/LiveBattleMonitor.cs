using System.IO;
using System.Threading.Channels;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 实时战斗监控。
/// </summary>
/// <remarks>
/// 把观测与解析串成一条不阻塞调用方的流水线。观测器的发现回调跑在轮询
/// 线程上，若直接在回调里解析回放，一次解析要跑几秒到几十秒，会把观测
/// 循环卡住，后续回放全部漏掉。因此发现只入队，解析交给独立的后台工作
/// 协程串行消费。
///
/// 串行而非并行：replayshark 是外部进程，同时开多个既抢 CPU 又抢磁盘，
/// 回放本来就是一局接一局产生，串行足够。
///
/// 停止后可再次启动：停止只取消消费协程并停掉观测，队列本身保持打开，
/// 这样界面上的开始/停止按钮可以反复切换。只有销毁才关闭队列。
/// </remarks>
public sealed class LiveBattleMonitor : IDisposable
{
    private readonly Func<string, CancellationToken, Task<IngestionResult>> ingest;
    private readonly Channel<string> queue;
    private readonly object stateLock = new();
    private readonly LogService? logs;
    private ReplayObserver? observer;
    private TaskCompletionSource idleSource = CreateIdleSource();
    private CancellationTokenSource? cts;
    private Task? worker;
    private int pending;
    private bool disposed;

    /// <summary>
    /// 构造监控。
    /// </summary>
    /// <param name="ingest">解析并入库的委托，通常是 BattleIngestionService.IngestAsync。</param>
    /// <param name="observer">回放观测器。测试时可为空，改用 Enqueue 直接投喂路径。</param>
    /// <param name="capacity">待处理队列上限。超出时丢弃本次投递。</param>
    /// <param name="logs">运行日志服务。可为空，测试场景不依赖日志。</param>
    public LiveBattleMonitor(
        Func<string, CancellationToken, Task<IngestionResult>> ingest,
        ReplayObserver? observer = null,
        int capacity = 128,
        LogService? logs = null)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "队列容量必须为正数。");
        }

        this.ingest = ingest;
        this.observer = observer;
        this.logs = logs;
        queue = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            // 队列满时 TryWrite 直接失败，由调用方决定丢弃。
            // 不用 DropOldest：丢弃发生在通道内部，待处理计数无法同步，空闲等待会算错。
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        if (observer is not null)
        {
            observer.ReplayDiscovered += Enqueue;
        }
    }

    /// <summary>
    /// 一局成功入库后触发。参数为该局的入库结果。
    /// </summary>
    public event Action<IngestionResult>? BattleIngested;

    /// <summary>
    /// 一局解析或入库失败时触发。参数为回放路径与失败原因。
    /// </summary>
    public event Action<string, string>? IngestionFailed;

    /// <summary>待处理队列中的回放数量。</summary>
    public int PendingCount => Volatile.Read(ref pending);

    /// <summary>是否正在消费。</summary>
    public bool IsRunning
    {
        get
        {
            lock (stateLock)
            {
                return worker is not null;
            }
        }
    }

    /// <summary>
    /// 启动监控。观测器存在时同时启动观测。重复调用无副作用。
    /// </summary>
    public void Start()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(LiveBattleMonitor));
        }

        lock (stateLock)
        {
            if (worker is not null)
            {
                return;
            }

            cts = new CancellationTokenSource();
            worker = Task.Run(() => ProcessLoopAsync(cts.Token));
        }

        observer?.Start();
    }

    /// <summary>
    /// 停止监控与观测，但保留队列，可再次启动。
    /// </summary>
    public void Stop()
    {
        observer?.Stop();

        Task? toWait;
        CancellationTokenSource? toDispose;
        lock (stateLock)
        {
            if (worker is null)
            {
                return;
            }

            toWait = worker;
            toDispose = cts;
            worker = null;
            cts = null;
        }

        toDispose?.Cancel();
        try
        {
            toWait.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 停止超时不阻断退出。
        }

        toDispose?.Dispose();

        // 停止后队列里的残留不再处理，重置空闲信号避免等待方挂住。
        ResetPending();
    }

    /// <summary>
    /// 换绑观测器。配置变更后观测目录随之变化，需要换一个观测器。
    /// </summary>
    /// <remarks>
    /// 不重建监控实例，是为了保住订阅者。事件订阅绑定在实例上，换实例会
    /// 让界面收不到入库通知。运行中换绑会自动恢复运行状态。
    /// </remarks>
    /// <param name="newObserver">新的观测器，可为空表示只保留队列消费。</param>
    public void Rebind(ReplayObserver? newObserver)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        bool wasRunning = IsRunning;
        Stop();

        if (observer is not null)
        {
            observer.ReplayDiscovered -= Enqueue;
            observer.Dispose();
        }

        if (newObserver is not null)
        {
            newObserver.ReplayDiscovered += Enqueue;
        }

        lock (stateLock)
        {
            observer = newObserver;
        }

        if (wasRunning)
        {
            Start();
        }
    }

    /// <summary>
    /// 把一份回放加入待处理队列。
    /// </summary>
    /// <remarks>
    /// 由观测线程调用，必须立即返回。队列满时丢弃本次投递，不阻塞观测循环。
    /// </remarks>
    public void Enqueue(string replayPath)
    {
        if (string.IsNullOrWhiteSpace(replayPath) || disposed)
        {
            return;
        }

        lock (stateLock)
        {
            if (pending == 0 && idleSource.Task.IsCompleted)
            {
                idleSource = CreateIdleSource();
            }

            pending++;
        }

        // 队列满或通道已关闭时写入失败，回退计数以免等待空闲时挂住。
        if (!queue.Writer.TryWrite(replayPath))
        {
            logs?.Warn($"待处理队列已满，丢弃回放: {Path.GetFileName(replayPath)}");
            CompleteOne();
        }
    }

    /// <summary>
    /// 等待队列清空。供测试与退出前收尾使用。
    /// </summary>
    public Task WaitForIdleAsync()
    {
        lock (stateLock)
        {
            return pending == 0 ? Task.CompletedTask : idleSource.Task;
        }
    }

    /// <summary>
    /// 后台消费协程。
    /// </summary>
    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out string? path))
                {
                    await ProcessOneAsync(path, cancellationToken).ConfigureAwait(false);
                    CompleteOne();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    private async Task ProcessOneAsync(string replayPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ingest(replayPath, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                logs?.Info(result.IsDuplicate
                    ? $"同局重复，已收敛: {Path.GetFileName(replayPath)}"
                    : $"对局已入库: {Path.GetFileName(replayPath)}");
                BattleIngested?.Invoke(result);
            }
            else
            {
                string reason = result.Message ?? "解析失败，未提供原因。";
                logs?.Warn($"回放处理失败: {Path.GetFileName(replayPath)} · {reason}");
                IngestionFailed?.Invoke(replayPath, reason);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单份回放失败不能中断整条流水线。
            logs?.Error($"回放处理异常: {Path.GetFileName(replayPath)} · {ex.Message}");
            IngestionFailed?.Invoke(replayPath, ex.Message);
        }
    }

    private void CompleteOne()
    {
        lock (stateLock)
        {
            if (pending > 0)
            {
                pending--;
            }

            if (pending == 0)
            {
                idleSource.TrySetResult();
            }
        }
    }

    private void ResetPending()
    {
        lock (stateLock)
        {
            pending = 0;
            idleSource.TrySetResult();
            idleSource = CreateIdleSource();
        }
    }

    private static TaskCompletionSource CreateIdleSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        Stop();

        if (observer is not null)
        {
            observer.ReplayDiscovered -= Enqueue;
            observer.Dispose();
        }

        queue.Writer.TryComplete();
    }
}
