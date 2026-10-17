using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Client.Services;

namespace WowsBattleIntel.Client.Tests;

public class LogServiceTests
{
    [Fact]
    public void Log_AppendsEntryWithLocalTimestampAndLevel()
    {
        var logs = new LogService();
        var before = DateTimeOffset.Now;

        logs.Warn("缓存未命中");

        var after = DateTimeOffset.Now;
        var snapshot = logs.Snapshot();

        Assert.Single(snapshot);
        var entry = snapshot[0];
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Equal("缓存未命中", entry.Message);
        Assert.InRange(entry.Timestamp, before, after);
    }

    [Fact]
    public void Log_RingBufferDropsOldestWhenFull()
    {
        var logs = new LogService();

        for (int i = 0; i < LogService.Capacity + 50; i++)
        {
            logs.Info($"条目 {i}");
        }

        var snapshot = logs.Snapshot();

        Assert.Equal(LogService.Capacity, snapshot.Count);
        Assert.Equal("条目 50", snapshot[0].Message);
        Assert.Equal($"条目 {LogService.Capacity + 49}", snapshot[^1].Message);
    }

    [Fact]
    public void Log_ConcurrentWritersKeepAllEntries()
    {
        var logs = new LogService();
        const int threads = 8;
        const int perThread = 60;

        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                logs.Info($"线程 {t} 条目 {i}");
            }
        });

        // 总量 480 未超容量，全部条目必须完整保留。
        Assert.Equal(threads * perThread, logs.Count);
    }

    [Fact]
    public void Log_ConcurrentOverflowStillKeepsCapacity()
    {
        var logs = new LogService();
        const int threads = 8;
        const int perThread = 200;

        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                logs.Info($"线程 {t} 条目 {i}");
            }
        });

        // 1600 条超容量，缓冲必须恰好停在容量上限，不丢不多。
        Assert.Equal(LogService.Capacity, logs.Count);
    }

    [Fact]
    public void Log_RaisesEntryLoggedEvent()
    {
        var logs = new LogService();
        LogEntry? received = null;
        logs.EntryLogged += e => received = e;

        logs.Error("测试错误");

        Assert.NotNull(received);
        Assert.Equal(LogLevel.Error, received!.Level);
        Assert.Equal("测试错误", received.Message);
    }

    [Fact]
    public void Snapshot_ReturnsIndependentCopy()
    {
        var logs = new LogService();
        logs.Info("第一条");

        var snapshot = logs.Snapshot();
        logs.Info("第二条");

        Assert.Single(snapshot);
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public void Log_RejectsEmptyMessage()
    {
        var logs = new LogService();

        Assert.Throws<ArgumentException>(() => logs.Info(""));
        Assert.Throws<ArgumentException>(() => logs.Info("   "));
    }

    /// <summary>记录写入的假文件写入器，用于验证 LogService 的转发行为。</summary>
    private sealed class RecordingWriter : ILogFileWriter
    {
        public List<LogEntry> Written { get; } = [];

        public int TrimCount { get; private set; }

        public bool Disposed { get; private set; }

        public string Directory => @"C:\fake\logs";

        public string CurrentFile => @"C:\fake\logs\app-20261017.log";

        public LogFileOptions Options { get; } = new(@"C:\fake\logs", 3, 10 * 1024 * 1024, LogRetentionMode.Both);

        public void Write(LogEntry entry) => Written.Add(entry);

        public void Flush()
        {
        }

        public void Trim() => TrimCount++;

        public void Trim(LogFileOptions overrideOptions) => TrimCount++;

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Log_PersistsEntriesToWriter()
    {
        var writer = new RecordingWriter();
        using var logs = new LogService(writer);

        logs.Info("落盘一条");
        logs.Error("落盘错误");

        Assert.Equal(2, writer.Written.Count);
        Assert.Equal(LogLevel.Error, writer.Written[^1].Level);
        Assert.True(logs.IsPersistent);
        Assert.Equal(writer.Directory, logs.LogDirectory);
    }

    [Fact]
    public void ReplaceHistory_FillsBufferWithoutPersisting()
    {
        var writer = new RecordingWriter();
        using var logs = new LogService(writer);

        logs.ReplaceHistory([
            new LogEntry(DateTimeOffset.Now.AddMinutes(-5), LogLevel.Warn, "上次运行"),
        ]);

        var snapshot = logs.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("上次运行", snapshot[0].Message);

        // 回填只进内存，不能把历史再写一遍磁盘。
        Assert.Empty(writer.Written);
    }

    [Fact]
    public void ReplaceHistory_TruncatesToCapacity()
    {
        using var logs = new LogService();

        logs.ReplaceHistory(Enumerable.Range(0, LogService.Capacity + 40)
            .Select(i => new LogEntry(DateTimeOffset.Now, LogLevel.Info, $"历史 {i}")));

        var snapshot = logs.Snapshot();
        Assert.Equal(LogService.Capacity, snapshot.Count);
        Assert.Equal("历史 40", snapshot[0].Message);
    }

    [Fact]
    public void TrimLogFiles_ForwardsToWriter()
    {
        var writer = new RecordingWriter();
        using var logs = new LogService(writer);

        logs.TrimLogFiles(writer.Options);

        Assert.Equal(1, writer.TrimCount);
    }

    [Fact]
    public void Dispose_ReleasesWriter()
    {
        var writer = new RecordingWriter();
        var logs = new LogService(writer);

        logs.Dispose();

        Assert.True(writer.Disposed);
    }

    [Fact]
    public void LogLineParser_RoundTripsWriterFormat()
    {
        var entry = new LogEntry(new DateTimeOffset(2026, 10, 17, 9, 5, 6, 789, TimeSpan.FromHours(8)), LogLevel.Warn, "解析回来");

        string line = $"{entry.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} [WARN] 解析回来";

        Assert.True(Services.LogLineParser.TryParse(line, out var parsed));
        Assert.Equal(LogLevel.Warn, parsed.Level);
        Assert.Equal("解析回来", parsed.Message);
        Assert.Equal(entry.Timestamp.LocalDateTime, parsed.Timestamp.LocalDateTime);
    }

    [Fact]
    public void LogLineParser_RejectsMalformedLines()
    {
        Assert.False(Services.LogLineParser.TryParse("随便一行", out _));
        Assert.False(Services.LogLineParser.TryParse("2026-10-17 09:05:06.789 [TRACE] 未知级别", out _));
        Assert.False(Services.LogLineParser.TryParse("2026-13-45 09:05:06.789 [INFO] 坏时间", out _));
    }
}
