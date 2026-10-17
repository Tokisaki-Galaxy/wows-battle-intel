using System.IO;
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
    public void RebindWriter_KeepsBufferAndEventSubscription()
    {
        var first = new RecordingWriter();
        var logs = new LogService(first);
        var received = new List<string>();
        logs.EntryLogged += e => received.Add(e.Message);

        logs.Info("换绑前");
        first.Dispose();
        first.Written.Clear();

        var second = new RecordingWriter();
        logs.RebindWriter(second);
        logs.Info("换绑后");

        // 事件订阅不因换绑失效，两条都该到达订阅方。
        Assert.Equal(["换绑前", "换绑后"], received);
        Assert.Single(second.Written);
        Assert.Equal("换绑后", second.Written[0].Message);
        Assert.True(first.Disposed);
    }

    [Fact]
    public void Log_SubscriberThrowing_DoesNotBreakCaller()
    {
        using var logs = new LogService();
        int healthy = 0;
        logs.EntryLogged += _ => throw new InvalidOperationException("坏订阅方");
        logs.EntryLogged += _ => healthy++;

        var exception = Record.Exception(() => logs.Info("一条"));

        Assert.Null(exception);
        Assert.Equal(1, healthy);
        Assert.Equal(1, logs.Count);
    }

    [Fact]
    public void LoadRecent_SkipsEntriesWrittenThisRun()
    {
        string dir = Path.Combine(TestServices.TestTempRoot, $"wbi-log-hist-{Guid.NewGuid():N}");
        var options = new LogFileOptions(dir, 3, 10 * 1024 * 1024, LogRetentionMode.Both);

        try
        {
            // 上一次运行留下的内容。
            using (var previous = new LogFileWriter(options))
            {
                previous.Write(new LogEntry(DateTimeOffset.Now.AddMinutes(-10), LogLevel.Warn, "上次运行留下"));
                previous.Flush();
            }

            // 本次运行：先回填，再写两条。
            using var logs = new LogService(new LogFileWriter(options));
            var history = logs.LoadRecent();
            logs.ReplaceHistory(history);
            logs.Info("本次第一条");
            logs.Info("本次第二条");

            var messages = logs.Snapshot().Select(e => e.Message).ToList();
            Assert.Single(history);
            Assert.Equal("上次运行留下", messages[0]);
            Assert.Equal(["本次第一条", "本次第二条"], messages.Skip(1));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响结论。
            }
        }
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
