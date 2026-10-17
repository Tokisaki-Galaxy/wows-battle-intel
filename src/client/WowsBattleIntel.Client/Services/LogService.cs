using System.IO;

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
///
/// 可选接入 <see cref="ILogFileWriter"/> 做落盘。落盘在缓冲之外独立处理，
/// 写盘失败不影响内存日志与事件。未接写入器时行为与纯内存模式一致。
/// </remarks>
public sealed class LogService : IDisposable
{
    /// <summary>环形缓冲容量，超出后丢弃最旧条目。</summary>
    public const int Capacity = 500;

    private readonly object bufferLock = new();
    private readonly Queue<LogEntry> buffer = new(Capacity);
    private readonly ILogFileWriter? fileWriter;

    /// <summary>
    /// 构造日志服务。
    /// </summary>
    /// <param name="fileWriter">落盘写入器，为 null 时只保留内存日志。</param>
    public LogService(ILogFileWriter? fileWriter = null)
    {
        this.fileWriter = fileWriter;
    }

    /// <summary>新条目写入后触发。事件在锁外触发，订阅方不得在回调里重入写日志。</summary>
    public event Action<LogEntry>? EntryLogged;

    /// <summary>日志目录。未接落盘时为空串。</summary>
    public string LogDirectory => fileWriter?.Directory ?? string.Empty;

    /// <summary>是否已接入落盘。</summary>
    public bool IsPersistent => fileWriter is not null;

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
    /// 落盘在事件之前，保证崩溃时磁盘上的记录不少于界面所见。
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

        // 写入器自身吞异常，这里不再包一层。
        fileWriter?.Write(entry);

        EntryLogged?.Invoke(entry);
    }

    /// <summary>
    /// 读取最近的历史日志，用于启动时把上次运行的内容回填到界面。
    /// </summary>
    /// <remarks>
    /// 只取最近一个文件，最多 <paramref name="maxEntries"/> 条，从尾往前解析。
    /// 任何 IO 异常都按无历史处理。
    /// </remarks>
    public IReadOnlyList<LogEntry> LoadRecent(int maxEntries = Capacity)
    {
        if (fileWriter is null || maxEntries <= 0)
        {
            return [];
        }

        try
        {
            fileWriter.Flush();
            string path = fileWriter.CurrentFile;
            if (!File.Exists(path))
            {
                return [];
            }

            var lines = new Queue<string>(maxEntries);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (lines.Count == maxEntries)
                    {
                        lines.Dequeue();
                    }

                    lines.Enqueue(line);
                }
            }

            var result = new List<LogEntry>(lines.Count);
            foreach (string line in lines)
            {
                if (LogLineParser.TryParse(line, out var entry))
                {
                    result.Add(entry);
                }
            }

            return result;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// 用历史日志填充内存缓冲。
    /// </summary>
    /// <remarks>
    /// 只动缓冲，不写盘也不触发 <see cref="EntryLogged"/>，供启动时回填上次运行的
    /// 现场。界面模型在构造时取一次快照，因此必须在窗口创建前调用。
    /// 不写盘是刻意的：每启动一次就把旧记录再追加一遍，日志会自我复制。
    /// </remarks>
    public void ReplaceHistory(IEnumerable<LogEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        lock (bufferLock)
        {
            buffer.Clear();
            foreach (var entry in history)
            {
                if (buffer.Count == Capacity)
                {
                    buffer.Dequeue();
                }

                buffer.Enqueue(entry);
            }
        }
    }

    /// <summary>
    /// 按指定口径清理日志文件。目录固定为当前正在写的目录。
    /// </summary>
    public void TrimLogFiles(LogFileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (fileWriter is null)
        {
            return;
        }

        // 只借口径，落盘目录以写入器自身为准，避免设置页手改了未保存的路径
        // 就把别处的文件误清掉。
        var scoped = options with { Directory = fileWriter.Directory };
        if (scoped == fileWriter.Options)
        {
            fileWriter.Trim();
            return;
        }

        // 口径不同就临开一个只做清理的写入器，用完即关，不动正在写的流。
        try
        {
            using var trimmer = new LogFileWriter(scoped);
        }
        catch (Exception)
        {
            // 清理失败不影响当前写入。
        }
    }

    /// <summary>取当前全部条目的快照，按写入顺序排列。返回副本，外部修改不影响缓冲。</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (bufferLock)
        {
            return [.. buffer];
        }
    }

    /// <summary>刷出未落盘的内容并释放写入器。</summary>
    public void Dispose()
    {
        fileWriter?.Dispose();
    }
}
