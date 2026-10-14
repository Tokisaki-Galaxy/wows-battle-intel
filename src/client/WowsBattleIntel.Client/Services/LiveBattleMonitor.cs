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
/// </remarks>
public sealed class LiveBattleMonitor : IDisposable
{
    private readonly ReplayObserver? observer;
    private readonly Func<string, CancellationToken, Task<IngestionResult>> ingest;
    private readonly Channel<string> queue;
    private readonly object idleLock = new();
    private readonly CancellationTokenSource cts = new();
    private TaskCompletionSource idleSource = CreateIdleSource();
    private Task? worker;
    private int pending;
    private bool disposed;

    /// <summary>
    /// 构造监控。
    /// </summary>
    /// <param name="ingest">解析并入库的委托，通常是 BattleIngestionService.IngestAsync。</param>
    /// <param name="observer">回放观测器。测试时可为空，改用 Enqueue 直接投喂路径。</param>
    /// <param name="capacity">待处理队列上限。超出时丢弃，避免长时间停机后堆积。</param>
    public LiveBattleMonitor(
        Func<string, CancellationToken, Task<IngestionResult>> ingest,
        ReplayObserver? observer = null,
        int capacity = 128)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "队列容量必须为正数。");
        }

        this.ingest = ingest;
        this.observer = observer;
        queue = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            // 队列满时丢弃最旧的一条。新回放比旧回放更有价值。
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
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

    /// <summary>
    /// 启动监控。观测器存在时同时启动观测。
    /// </summary>
    public void Start()
    {
        if (worker is not null)
        {
            return;
        }

        if (observer is not null)
        {
            observer.ReplayDiscovered += Enqueue;
            observer.Start();
        }

        worker = Task.Run(() => ProcessLoopAsync(cts.Token));
    }

    /// <summary>
    /// 把一份回放加入待处理队列。
    /// </summary>
    /// <remarks>
    /// 由观测线程调用，必须立即返回。队列满时 Channel 会丢弃最旧的一条，
    /// 这里不做等待。
    /// </remarks>
    public void Enqueue(string replayPath)
    {
        if (string.IsNullOrWhiteSpace(replayPath) || disposed)
        {
            return;
        }

        lock (idleLock)
        {
            if (pending == 0 && idleSource.Task.IsCompleted)
            {
                idleSource = CreateIdleSource();
            }

            pending++;
        }

        if (!queue.Writer.TryWrite(replayPath))
        {
            // 写入失败说明通道已关闭，回退计数以免等待空闲时挂住。
            CompleteOne();
        }
    }

    /// <summary>
    /// 等待队列清空。供测试与退出前收尾使用。
    /// </summary>
    public Task WaitForIdleAsync()
    {
        lock (idleLock)
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
                BattleIngested?.Invoke(result);
            }
            else
            {
                IngestionFailed?.Invoke(replayPath, result.Message ?? "解析失败，未提供原因。");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单份回放失败不能中断整条流水线。
            IngestionFailed?.Invoke(replayPath, ex.Message);
        }
    }

    private void CompleteOne()
    {
        lock (idleLock)
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

        if (observer is not null)
        {
            observer.ReplayDiscovered -= Enqueue;
            observer.Dispose();
        }

        queue.Writer.TryComplete();
        cts.Cancel();

        try
        {
            worker?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 退出阶段不因收尾超时阻断。
        }

        cts.Dispose();
    }
}
