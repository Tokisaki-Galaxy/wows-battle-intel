using System.IO;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Client.ViewModels;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 当前对局生命周期测试：观测器捕获 tempArenaInfo → 实时区出现 → 列表重建
/// 后仍在 → 文件删除后收起。锁住「当前对局消失」这个回归。
/// </summary>
/// <remarks>
/// 观测事件经同步上下文回投界面线程，异步测试里 xunit 装了同步上下文，
/// 事件落地晚于 ScanOnce 返回，断言前必须轮询等待。
/// </remarks>
public class PreBattleLifecycleTests : IDisposable
{
    private readonly string gamePath;
    private readonly string replayDir;

    public PreBattleLifecycleTests()
    {
        gamePath = Path.Combine(TestServices.TestTempRoot, $"wbi-prelive-{Guid.NewGuid():N}");
        replayDir = Path.Combine(gamePath, "replays");
        Directory.CreateDirectory(replayDir);
        Directory.CreateDirectory(Path.Combine(gamePath, "profile"));
        File.WriteAllLines(
            Path.Combine(gamePath, "profile", "clientrunner.log"),
            ["[2026-10-16 13:15:22] [  INFO  ] Selected realm: asia"]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(gamePath, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论。
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"等待超时: {description}");
    }

    private static string ArenaJson() => """
        {
          "matchGroup": "pvp",
          "gameType": "RandomBattle",
          "dateTime": "16.10.2026 13:00:00",
          "vehicles": [
            {"id": 500000001, "name": "AllyPlayer", "relation": 0, "shipId": "4286581900"},
            {"id": 500000002, "name": "EnemyPlayer", "relation": 2, "shipId": "4286581901"}
          ]
        }
        """;

    private string WriteArena()
    {
        string path = Path.Combine(replayDir, "tempArenaInfo.json");
        File.WriteAllText(path, ArenaJson());
        return path;
    }

    private AppConfig CreateConfig() => new()
    {
        GamePath = gamePath,
        DatabasePath = TestServices.NewDatabasePath(),
    };

    [Fact]
    public async Task CurrentBattle_AppearsOnCapture_SurvivesListRebuild_EndsOnFileDeletion()
    {
        string arenaPath = WriteArena();
        using var holder = await TestServices.CreateAsync(CreateConfig());
        var vm = new MainViewModel(holder.Services);

        // 观测器捕获赛前名单。
        holder.Services.ArenaWatcher.ScanOnce();
        await WaitUntilAsync(() => vm.HasLiveBattles, "当前对局出现");

        var live = Assert.Single(vm.LiveBattles);
        Assert.Equal("AllyPlayer", live.Battle.Allies.First().PlayerName);
        // 服务器从 clientrunner.log 探测，不是 Auto。
        Assert.Equal(GameServer.Asia, live.Battle.Server);

        // 列表重建（正式回放入库会走这条路径）不得抹掉当前对局。
        await vm.LoadRecentBattlesAsync();
        Assert.Single(vm.LiveBattles);
        Assert.True(vm.HasLiveBattles);

        // 对局结束：tempArenaInfo 被游戏删除，实时区收起。
        File.Delete(arenaPath);
        holder.Services.ArenaWatcher.ScanOnce();
        await WaitUntilAsync(() => !vm.HasLiveBattles, "当前对局收起");

        Assert.Empty(vm.LiveBattles);
    }

    [Fact]
    public async Task CurrentBattle_ClearedOnStopObservation()
    {
        WriteArena();
        using var holder = await TestServices.CreateAsync(CreateConfig());
        var vm = new MainViewModel(holder.Services);

        holder.Services.ArenaWatcher.ScanOnce();
        await WaitUntilAsync(() => vm.HasLiveBattles, "当前对局出现");

        vm.StartObservation();
        vm.StopObservation();

        await WaitUntilAsync(() => !vm.HasLiveBattles, "停止观测后收起");
        Assert.Empty(vm.LiveBattles);
    }
}
