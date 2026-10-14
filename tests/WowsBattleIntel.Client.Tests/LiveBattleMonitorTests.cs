using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Tests;

public class LiveBattleMonitorTests
{
    private static BattleRecord MakeBattle(string id = "battle-001") => new()
    {
        BattleId = id,
        StartedAt = DateTimeOffset.UtcNow,
        MapId = "test-map",
        Outcome = BattleOutcome.Win,
    };

    [Fact]
    public async Task Enqueue_SuccessfulIngestion_RaisesEvent()
    {
        var ingested = new TaskCompletionSource<IngestionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var monitor = new LiveBattleMonitor((path, _) =>
        {
            var result = IngestionResult.Succeeded(MakeBattle(), null);
            ingested.TrySetResult(result);
            return Task.FromResult(result);
        });

        monitor.Start();
        monitor.Enqueue("a.wowsreplay");

        var result = await ingested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.Equal("battle-001", result.Battle!.BattleId);
    }

    [Fact]
    public async Task Enqueue_FailedIngestion_RaisesFailureEvent()
    {
        var failed = new TaskCompletionSource<(string Path, string Message)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var monitor = new LiveBattleMonitor((path, _) =>
            Task.FromResult(IngestionResult.Failed("版本不匹配")));
        monitor.IngestionFailed += (path, message) => failed.TrySetResult((path, message));

        monitor.Start();
        monitor.Enqueue("bad.wowsreplay");

        var (failedPath, message) = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("bad.wowsreplay", failedPath);
        Assert.Equal("版本不匹配", message);
    }

    [Fact]
    public async Task Enqueue_ThrowingIngestion_DoesNotStopPipeline()
    {
        var calls = 0;
        var second = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var monitor = new LiveBattleMonitor((path, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("模拟解析异常");
            }

            second.TrySetResult();
            return Task.FromResult(IngestionResult.Succeeded(MakeBattle(), null));
        });

        monitor.Start();
        monitor.Enqueue("first.wowsreplay");
        monitor.Enqueue("second.wowsreplay");

        // 第一份抛异常后，第二份仍应被处理。
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task WaitForIdle_ReturnsAfterQueueDrained()
    {
        int processed = 0;
        using var monitor = new LiveBattleMonitor((_, _) =>
        {
            Interlocked.Increment(ref processed);
            return Task.FromResult(IngestionResult.Succeeded(MakeBattle(), null));
        });

        monitor.Start();
        monitor.Enqueue("a.wowsreplay");
        monitor.Enqueue("b.wowsreplay");
        monitor.Enqueue("c.wowsreplay");

        await monitor.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, monitor.PendingCount);
        Assert.Equal(3, processed);
    }

    [Fact]
    public async Task WaitForIdle_NoPendingWork_CompletesImmediately()
    {
        using var monitor = new LiveBattleMonitor((_, _) =>
            Task.FromResult(IngestionResult.Succeeded(MakeBattle(), null)));

        monitor.Start();

        await monitor.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, monitor.PendingCount);
    }

    [Fact]
    public void Enqueue_BlankPath_IsIgnored()
    {
        using var monitor = new LiveBattleMonitor((_, _) =>
            Task.FromResult(IngestionResult.Succeeded(MakeBattle(), null)));

        monitor.Enqueue("   ");
        Assert.Equal(0, monitor.PendingCount);
    }

    [Fact]
    public async Task Dispose_StopsAcceptingWork()
    {
        using var monitor = new LiveBattleMonitor((_, _) =>
            Task.FromResult(IngestionResult.Succeeded(MakeBattle(), null)));
        monitor.Start();
        monitor.Dispose();

        // 销毁后再投递应被忽略，不抛异常。
        monitor.Enqueue("late.wowsreplay");
        await Task.CompletedTask;
    }
}
