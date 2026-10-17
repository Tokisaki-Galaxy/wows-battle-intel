using System.Globalization;
using System.IO;
using System.Text;
using WowsBattleIntel.Client.Configuration;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 运行日志落盘实现。
/// </summary>
/// <remarks>
/// 按本地日期滚动，文件名形如 app-yyyyMMdd.log，一行一条。
///
/// 写入是同步的但只在内存里拼完行就算返回：先追加到内存缓冲，到 64KB 或
/// 每 5 秒才真正刷到磁盘。日志量小，这样既不会拖慢界面线程，崩溃时最多
/// 丢最后几秒的内容。
///
/// 任何 IO 异常都被吞掉并置为降级状态，之后只保留内存日志。写日志本身
/// 失败再往上抛，会把调用方的业务线程一起带崩，得不偿失。
/// </remarks>
public sealed class LogFileWriter : ILogFileWriter
{
    /// <summary>文件名前缀。</summary>
    public const string FilePrefix = "app-";

    /// <summary>文件名后缀。</summary>
    public const string FileSuffix = ".log";

    /// <summary>缓冲刷盘阈值字节。</summary>
    public const long FlushThresholdBytes = 64 * 1024;

    /// <summary>缓冲刷盘间隔。</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    /// <summary>裁剪尾部时至少保留的部分，避免把当天文件削成空文件。</summary>
    public const long TrimKeepBytes = 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>解析文件名中的日期。同时用于裁剪时按文件内容的时间戳过滤。</summary>
    private static readonly string FileNameFormat = "yyyyMMdd";

    private readonly LogFileOptions options;
    private readonly Func<DateTimeOffset> clock;

    private readonly object writeLock = new();
    private readonly StringBuilder buffer = new();

    private string currentFile = string.Empty;

    private FileStream? stream;
    private FileStream? readStream;

    // 当前追加流的长度，用于刷盘后低成本判断是否超过体积上限。
    private long currentStreamLength;
    private long lastFlushAt;
    private bool degraded;

    /// <summary>
    /// 构造写入器。目录不存在时立即创建。
    /// </summary>
    /// <param name="options">落盘选项。</param>
    /// <param name="timeProvider">时间来源，测试可注入固定时钟。</param>
    public LogFileWriter(LogFileOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        var source = timeProvider ?? TimeProvider.System;
        clock = () => NowIn(source);

        Directory = options.Directory;
        var now = clock();
        lastFlushAt = Environment.TickCount64;
        currentFile = Path.Combine(Directory, BuildFileName(now));

        OpenForAppend();
        Trim();
    }

    /// <inheritdoc />
    public string Directory { get; }

    /// <inheritdoc />
    public string CurrentFile => currentFile;

    /// <summary>是否已因 IO 失败降级为只写内存。</summary>
    public bool IsDegraded => degraded;

    /// <inheritdoc />
    public void Write(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            lock (writeLock)
            {
                if (degraded)
                {
                    return;
                }

                var entryDate = LocalDate(entry.Timestamp);
                if (entryDate > LocalDate(clock()))
                {
                    RollOver(entry.Timestamp);
                }

                buffer.Append(FormatLine(entry));

                bool due = buffer.Length >= FlushThresholdBytes
                    || Environment.TickCount64 - lastFlushAt >= (long)FlushInterval.TotalMilliseconds;
                if (due)
                {
                    FlushCore();

                    // 体积判定紧跟刷盘：此时缓冲已清空，文件长度就是实际占用。
                    TrimIfSizeBudgetExceeded();
                }
            }
        }
        catch (Exception)
        {
            // 落盘失败只降级，不影响调用方。缓冲里未刷出的内容直接丢弃，
            // 内存日志仍由 LogService 持有。
            degraded = true;
        }
    }

    /// <summary>
    /// 每次刷盘后按体积上限做一次轻量判定，超限才真正列目录清理。
    /// </summary>
    /// <remarks>
    /// 只在刷盘时机检查，避免每条日志都去枚举文件夹。刷盘由写满 64KB 或
    /// 满 5 秒触发，因此占用超出上限的幅度不超过一个刷盘周期的写入量。
    /// </remarks>
    private void TrimIfSizeBudgetExceeded()
    {
        if (options.MaxBytes <= 0 || options.Mode == LogRetentionMode.Days)
        {
            return;
        }

        if (currentStreamLength <= options.MaxBytes)
        {
            return;
        }

        try
        {
            TrimCore(clock().DateTime);
        }
        catch (Exception)
        {
            // 清理失败不能连累正在进行的那次写入，留到下次刷盘再试。
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        try
        {
            lock (writeLock)
            {
                if (!degraded)
                {
                    FlushCore();
                }
            }
        }
        catch (Exception)
        {
            degraded = true;
        }
    }

    /// <inheritdoc />
    public LogFileOptions Options => options;

    /// <inheritdoc />
    public void Trim()
    {
        try
        {
            lock (writeLock)
            {
                TrimCore(clock().DateTime);
            }
        }
        catch (Exception)
        {
            degraded = true;
        }
    }

    /// <inheritdoc />
    public void Trim(LogFileOptions limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        try
        {
            lock (writeLock)
            {
                TrimCore(clock().DateTime, limits);
            }
        }
        catch (Exception)
        {
            degraded = true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Flush();

        try
        {
            readStream?.Dispose();
            stream?.Dispose();
        }
        catch (Exception)
        {
            // 关闭失败不影响退出。
        }

        readStream = null;
        stream = null;
    }

    /// <summary>取本地时间。与时区服务分开处理，测试注入的时钟能给出确定的本地时间。</summary>
    private static DateTimeOffset NowIn(TimeProvider provider) =>
        TimeZoneInfo.ConvertTime(provider.GetUtcNow(), provider.LocalTimeZone);

    /// <summary>取条目在系统本地时区下的日期，用于判断是否跨天。</summary>
    private static DateTime LocalDate(DateTimeOffset moment) =>
        TimeZoneInfo.ConvertTime(moment, TimeZoneInfo.Local).Date;

    /// <summary>单条日志的文本行。</summary>
    private static string FormatLine(LogEntry entry) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{TimeZoneInfo.ConvertTime(entry.Timestamp, TimeZoneInfo.Local).DateTime:yyyy-MM-dd HH:mm:ss.fff} [{LevelText(entry.Level)}] {entry.Message}\n");

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO",
    };

    private static string BuildFileName(DateTimeOffset moment) =>
        $"{FilePrefix}{moment.DateTime.ToString(FileNameFormat, CultureInfo.InvariantCulture)}{FileSuffix}";

    /// <summary>解析文件名的日期部分，不是本应用命名的文件返回 null。</summary>
    internal static DateTime? TryParseFileName(string path)
    {
        string name = Path.GetFileName(path);
        if (!name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string core = name[FilePrefix.Length..^FileSuffix.Length];
        return DateTime.TryParseExact(
            core,
            FileNameFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>切到新一天的文件。旧文件先刷盘再关闭。</summary>
    private void RollOver(DateTimeOffset moment)
    {
        FlushCore();
        stream?.Dispose();
        stream = null;

        currentFile = Path.Combine(Directory, BuildFileName(moment));
        OpenForAppend();
        TrimCore(moment.DateTime);
    }

    /// <summary>打开当日文件的追加流。读日期由文件名决定，内容跨进程接续。</summary>
    private void OpenForAppend()
    {
        System.IO.Directory.CreateDirectory(Directory);
        stream = new FileStream(
            currentFile,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        currentStreamLength = stream.Length;
    }

    /// <summary>
    /// 临时释放正在写的追加句柄，换成一个共享读写句柄返回给调用方。
    /// </summary>
    /// <remarks>
    /// Windows 上带 Data 的追加句柄不允许另一个句柄改写同一文件，截断前必须先
    /// 换掉。调用方用完该句柄后调用 <see cref="RestoreAppend"/> 接续写入；
    /// 即使替换读写句柄失败，追加流也会在 RestoreAppend 里重新打开。
    /// </remarks>
    private FileStream SwapToReadHandle()
    {
        FlushCore();
        stream?.Dispose();
        stream = null;

        readStream = new FileStream(
            currentFile,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        return readStream;
    }

    /// <summary>关闭临时读句柄，重新打开追加流。</summary>
    private void RestoreAppend()
    {
        try
        {
            readStream?.Dispose();
        }
        catch (IOException)
        {
            // 读句柄关不掉也不影响下一步重新打开。
        }

        readStream = null;
        OpenForAppend();
    }

    /// <summary>把缓冲刷进文件。调用方持锁。</summary>
    private void FlushCore()
    {
        if (stream is null || buffer.Length == 0)
        {
            buffer.Clear();
            return;
        }

        byte[] bytes = Utf8NoBom.GetBytes(buffer.ToString());
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: false);
        buffer.Clear();
        currentStreamLength = stream.Length;
        lastFlushAt = Environment.TickCount64;
    }

    /// <summary>
    /// 按口径清理文件。调用方持锁。
    /// </summary>
    /// <remarks>
    /// 先删超期文件，再按体积从最旧删起。总字节按磁盘实际大小统计，
    /// 不含尚未刷出的缓冲。裁剪到今日文件时从头截断，保留最近的尾部，
    /// 至少留 <see cref="TrimKeepBytes"/>，不会把正在写的文件削成空的。
    /// </remarks>
    private void TrimCore(DateTime now) => TrimCore(now, options);

    /// <summary>
    /// 按给定选项清理文件。调用方持锁。
    /// </summary>
    /// <remarks>
    /// 先删超期文件，再按体积从最旧删起。总字节按磁盘实际大小统计。
    /// 总占用含正在写的文件时，正在写的那份只从头截断保留尾部，不删除；
    /// 其余文件整份删除，保证最终占用落在上限以内。
    /// </remarks>
    private void TrimCore(DateTime now, LogFileOptions limits)
    {
        bool applyDays = limits.RetentionDays > 0 && limits.Mode != LogRetentionMode.Size;
        bool applySize = limits.MaxBytes > 0 && limits.Mode != LogRetentionMode.Days;
        if (!applyDays && !applySize)
        {
            return;
        }

        FlushCore();
        var files = ListLogFiles();

        if (applyDays)
        {
            DateTime cutoff = now.Date.AddDays(-limits.RetentionDays);
            foreach (var file in files.Where(f => f.Date < cutoff).ToList())
            {
                TryDelete(file.Path);
                files.Remove(file);
            }
        }

        if (!applySize)
        {
            return;
        }

        long limit = Math.Max(limits.MaxBytes, TrimKeepBytes);
        long total = files.Sum(f => f.Length);
        if (total <= limit)
        {
            return;
        }

        // 从最旧删起。当前文件排在最后，前面删完仍超限时对它做截断。
        bool swapped = false;
        try
        {
            foreach (var file in files.OrderBy(f => f.Date))
            {
                if (total <= limit)
                {
                    break;
                }

                if (!IsCurrent(file.Path))
                {
                    total -= file.Length;
                    TryDelete(file.Path);
                    continue;
                }

                // 正在写的文件只截断不删除。先换成共享读句柄，Windows 才允许改写。
                swapped = true;
                var target = SwapToReadHandle();
                total = total - file.Length + TrimTail(target, limit);
            }
        }
        finally
        {
            if (swapped)
            {
                RestoreAppend();
            }
        }
    }

    /// <summary>判断是否为本实例正在写的文件。</summary>
    private bool IsCurrent(string path) =>
        string.Equals(path, CurrentFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从文件头部截断，只留最后至多 keepBytes 字节，且从完整行开始。
    /// </summary>
    /// <returns>截断后文件剩余字节数。</returns>
    /// <remarks>
    /// keepBytes 是相对整份日志总额算出的余量，可能小于一行，此时保留最后一行，
    /// 不做对齐修正，保证占用不超出上限。
    /// </remarks>
    private static long TrimTail(FileStream handle, long keepBytes)
    {
        long length = handle.Length;
        if (length <= keepBytes)
        {
            return length;
        }

        long start = length - Math.Max(keepBytes, 1);

        // 把起点推到下一行的行首，避免留下半行。
        int b;
        handle.Seek(start, SeekOrigin.Begin);
        while ((b = handle.ReadByte()) >= 0 && b != (byte)'\n')
        {
            start++;
        }

        if (start >= length)
        {
            // 余量不足一行，退回最后一行整行。
            start = FindLastLineStart(handle);
            if (start >= length)
            {
                return length;
            }
        }

        byte[] tail = new byte[length - start];
        handle.Seek(start, SeekOrigin.Begin);
        handle.ReadExactly(tail);

        // 起点落在换行符上时，去掉这个分隔符，保留的行从下一行开头开始。
        if (tail.Length > 0 && tail[0] == (byte)'\n')
        {
            tail = tail[1..];
        }

        // 读写句柄原地重写不可靠，改由一个新建的截断句柄整体重写。
        // 调用方随后调 RestoreAppend 恢复追加模式，文件位置自然回到末尾。
        string path = handle.Name;
        handle.Dispose();

        using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            target.Write(tail, 0, tail.Length);
            target.Flush(flushToDisk: true);
        }

        return tail.Length;
    }

    /// <summary>找最后一行（不超过一行）的开始位置。</summary>
    private static long FindLastLineStart(FileStream source)
    {
        long position = source.Length - 1;

        // 跳过文件末尾的换行符。
        while (position > 0 && ReadByteAt(source, position) == (byte)'\n')
        {
            position--;
        }

        while (position > 0 && ReadByteAt(source, position - 1) != (byte)'\n')
        {
            position--;
        }

        return position;
    }

    private static int ReadByteAt(FileStream source, long position)
    {
        source.Seek(position, SeekOrigin.Begin);
        return source.ReadByte();
    }

    private List<LogFileInfo> ListLogFiles()
    {
        var result = new List<LogFileInfo>();
        var directory = new System.IO.DirectoryInfo(Directory);
        if (!directory.Exists)
        {
            return result;
        }

        foreach (var file in directory.EnumerateFiles($"{FilePrefix}*{FileSuffix}"))
        {
            DateTime? date = TryParseFileName(file.Name);
            if (date is null)
            {
                continue;
            }

            // 正在写的文件刚刷过盘，FileInfo 的长度可能还是旧缓存值，
            // 这类文件直接向文件系统取实际大小。
            long length = string.Equals(file.FullName, currentFile, StringComparison.OrdinalIgnoreCase)
                ? new FileInfo(file.FullName).Length is var cached && cached > 0
                    ? cached
                    : currentStreamLength
                : file.Length;

            result.Add(new LogFileInfo(file.FullName, date.Value, length));
        }

        return result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 另一个实例正在写同一个文件时删不掉，跳过即可，下次清理再试。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record LogFileInfo(string Path, DateTime Date, long Length);
}
