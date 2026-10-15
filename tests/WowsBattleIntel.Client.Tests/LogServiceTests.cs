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
}
