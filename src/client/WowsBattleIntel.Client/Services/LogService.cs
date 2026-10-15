namespace WowsBattleIntel.Client.Services;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    /// <summary>普通流程信息。</summary>
    Info,

    /// <summary>可恢复的异常，功能继续运行。</summary>
    Warn,

    /// <summary>失败，功能受影响。</summary>
    Error,
}

/// <summary>单条运行日志。时间戳为写入时的本地时间。</summary>
/// <param name="Timestamp">写入时间，本地时间。</param>
/// <param name="Level">级别。</param>
/// <param name="Message">消息文本。</param>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message);

/// <summary>
/// 运行日志服务。
/// </summary>
/// <remarks>
/// 进程内环形缓冲，容量固定 500 条，超出丢弃最旧。写入可能来自后台线程，
/// 缓冲用锁保护；新条目通过事件通知，订阅方自行封送到界面线程。
/// </remarks>
public sealed class LogService
{
    /// <summary>环形缓冲容量，超出后丢弃最旧条目。</summary>
    public const int Capacity = 500;

    private readonly object bufferLock = new();
    private readonly Queue<LogEntry> buffer = new(Capacity);

    /// <summary>新条目写入后触发。事件在锁外触发，订阅方不得在回调里重入写日志。</summary>
    public event Action<LogEntry>? EntryLogged;

    /// <summary>当前缓冲内条目数。</summary>
    public int Count
    {
        get
        {
            lock (bufferLock)
            {
                return buffer.Count;
            }
        }
    }

    /// <summary>写入一条 Info 日志。</summary>
    public void Info(string message) => Log(LogLevel.Info, message);

    /// <summary>写入一条 Warn 日志。</summary>
    public void Warn(string message) => Log(LogLevel.Warn, message);

    /// <summary>写入一条 Error 日志。</summary>
    public void Error(string message) => Log(LogLevel.Error, message);

    /// <summary>写入一条指定级别的日志。</summary>
    /// <remarks>
    /// 缓冲操作在锁内完成，事件在锁外触发，回调耗时不会拖住并发写入方。
    /// </remarks>
    public void Log(LogLevel level, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        lock (bufferLock)
        {
            if (buffer.Count == Capacity)
            {
                buffer.Dequeue();
            }

            buffer.Enqueue(entry);
        }

        EntryLogged?.Invoke(entry);
    }

    /// <summary>取当前全部条目的快照，按写入顺序排列。返回副本，外部修改不影响缓冲。</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (bufferLock)
        {
            return [.. buffer];
        }
    }
}
