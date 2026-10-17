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

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        string description,
        int timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
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
        Assert.Equal("AllyPlayer", live.Battle!.Allies.First().PlayerName);
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
    public async Task CurrentBattle_ClearedOnConfigRebind()
    {
        WriteArena();
        using var holder = await TestServices.CreateAsync(CreateConfig());
        var vm = new MainViewModel(holder.Services);

        holder.Services.ArenaWatcher.ScanOnce();
        await WaitUntilAsync(() => vm.HasLiveBattles, "当前对局出现");

        // 配置换绑会重建观测器，旧的当前对局不再有事件驱动，应主动收起。
        await vm.Settings.SaveAsync();
        await WaitUntilAsync(() => !vm.HasLiveBattles, "换绑后收起");
        Assert.Empty(vm.LiveBattles);
    }

    [Fact]
    public async Task CurrentBattle_LeavingPreBattleClearsFetchProgress()
    {
        // 第一局的补拉进度提示必须清掉，不留旧进度。
        WriteArena();
        using var holder = await TestServices.CreateAsync(CreateConfig());

        await holder.Services.Repository.SaveBattleAsync(new BattleRecord
        {
            BattleId = "hist-status",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            MapId = "spaces/50_Gold_harbor",
            BattleType = BattleType.Random,
            Server = GameServer.Asia,
            Outcome = BattleOutcome.Win,
            Players =
            [
                new PlayerRecord
                {
                    AccountId = "1001", PlayerName = "HistAlly", Server = GameServer.Asia,
                    IsAlly = true, ShipClass = ShipClass.Battleship, ShipTier = 10,
                },
            ],
        });

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();

        // 先切到历史回放再切回赛前对局，详情区重建后观察补拉进度。
        // 赛前链路会真的进入补拉，顶栏出现“正在补充”。测试环境网络必失败，
        // 补拉会在一段时间后自己收尾，必须在它收尾前切走才对得上。
        holder.Services.ArenaWatcher.ScanOnce();
        await WaitUntilAsync(() => vm.HasLiveBattles, "当前对局出现");
        var live = vm.LiveBattles[0];
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "历史对局详情加载");
        vm.SelectedLiveBattle = live;
        await WaitUntilAsync(() => vm.Allies.Count > 0, "赛前名单灌入详情区");

        // 等收敛循环跑出第一版倒计时，此时补拉已经跑过一轮。
        await WaitUntilAsync(
            () => vm.CountdownText.Length > 0,
            "赛前倒计时出现",
            timeoutSeconds: 30);

        // 再切走，进度提示必须清掉。收敛循环被取消后还会走完收尾，投递是
        // 异步的，短暂延迟后才生效，给一个短窗口等它落地。
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(
            () => vm.Allies.Count > 0 && vm.CountdownText.Length == 0,
            "历史对局详情加载并收起赛前文案");
        await WaitUntilAsync(
            () => !vm.StatusMessage.StartsWith("正在补充", StringComparison.Ordinal),
            "换局后补拉提示消失",
            timeoutSeconds: 15);
        Assert.False(vm.StatusMessage.StartsWith("正在补充", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CurrentBattle_PreBattleLoopConvergesAndShowsRoster()
    {
        // 代次必须在 SelectedBattle 赋值之后再取。旧实现取在赋值之前，
        // 赋值同步触发 LoadBattleDetailsAsync 会递增 detailsVersion，
        // 导致收敛循环首轮 IsDetailsCurrent 即不成立、立刻退出，名单与
        // 预测都留不下。这里锁住"名单确实灌进详情区"。
        WriteArena();
        using var holder = await TestServices.CreateAsync(CreateConfig());
        var vm = new MainViewModel(holder.Services);

        holder.Services.ArenaWatcher.ScanOnce();

        await WaitUntilAsync(() => vm.Allies.Count > 0, "赛前名单灌入详情区");
        Assert.Single(vm.Allies);
        Assert.Single(vm.Enemies);
        Assert.Equal("AllyPlayer", vm.Allies[0].PlayerName);
    }

    [Fact]
    public async Task CurrentBattle_PredictionSurvivesSwitchToHistoryAndBack()
    {
        // 切到历史回放再切回当前对局，右侧预测不得变成 --。
        // 根因：赛前对局没有入库记录，详情区重建时若直接清空预测再查库，
        // 查不到就会留空。收敛循环必须自己持有预测，重建时用回它。
        WriteArena();
        using var holder = await TestServices.CreateAsync(CreateConfig());

        // 种一局历史回放，供切换用。
        await holder.Services.Repository.SaveBattleAsync(new BattleRecord
        {
            BattleId = "hist-1",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            MapId = "spaces/50_Gold_harbor",
            BattleType = BattleType.Random,
            Server = GameServer.Asia,
            Outcome = BattleOutcome.Win,
            Players =
            [
                new PlayerRecord
                {
                    AccountId = "1001", PlayerName = "HistAlly", Server = GameServer.Asia,
                    IsAlly = true, ShipClass = ShipClass.Battleship, ShipTier = 10,
                },
            ],
        });

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();

        holder.Services.ArenaWatcher.ScanOnce();
        await WaitUntilAsync(() => vm.HasLiveBattles, "当前对局出现");

        // 等收敛循环跑出第一版预测。
        await WaitUntilAsync(
            () => vm.PredictedWinrateText != "-",
            "赛前预测落定",
            timeoutSeconds: 30);

        var live = vm.LiveBattles[0];

        // 切到历史回放。
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "历史对局详情加载");

        // 切回当前对局，预测必须还在。
        vm.SelectedLiveBattle = live;
        await WaitUntilAsync(
            () => ReferenceEquals(vm.SelectedBattle, live.Battle),
            "切回当前对局");

        Assert.NotEqual("-", vm.PredictedWinrateText);
        Assert.NotEqual("-", vm.TierText);
    }

    [Fact]
    public async Task CurrentBattle_LoopStopsWithDangerColorAfterNoProgress()
    {
        // 测试环境网络必失败，补拉永远拉不到数据。连续多轮无进展后循环
        // 必须停止并把倒计时转成危险色，不能无限空转。
        WriteArena();
        using var holder = await TestServices.CreateAsync(CreateConfig());
        var vm = new MainViewModel(holder.Services);

        holder.Services.ArenaWatcher.ScanOnce();

        await WaitUntilAsync(
            () => vm.CountdownBrushKey == "DangerBrush",
            "连续无进展后转危险色",
            timeoutSeconds: 60);

        Assert.Contains("已停止", vm.CountdownText);
    }

    [Fact]
    public async Task CurrentBattle_CountdownHiddenWhenViewingHistory()
    {
        // 赛前补拉提示只属于当前对局。切到历史回放后，详情区不应再挂着
        // "补拉受阻"之类赛前文案。
        WriteArena();
        using var holder = await TestServices.CreateAsync(CreateConfig());

        await holder.Services.Repository.SaveBattleAsync(new BattleRecord
        {
            BattleId = "hist-countdown",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            MapId = "spaces/50_Gold_harbor",
            BattleType = BattleType.Random,
            Server = GameServer.Asia,
            Outcome = BattleOutcome.Win,
            Players =
            [
                new PlayerRecord
                {
                    AccountId = "1001", PlayerName = "HistAlly", Server = GameServer.Asia,
                    IsAlly = true, ShipClass = ShipClass.Battleship, ShipTier = 10,
                },
            ],
        });

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();

        holder.Services.ArenaWatcher.ScanOnce();
        await WaitUntilAsync(() => vm.HasLiveBattles, "当前对局出现");

        // 等倒计时文案先落定，确保确实有一段赛前状态在展示。
        await WaitUntilAsync(
            () => vm.CountdownText.Length > 0,
            "赛前倒计时出现",
            timeoutSeconds: 30);

        var live = vm.LiveBattles[0];
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "历史对局详情加载");

        Assert.Equal(string.Empty, vm.CountdownText);

        // 切回当前对局，赛前文案恢复。
        vm.SelectedLiveBattle = live;
        await WaitUntilAsync(() => vm.CountdownText.Length > 0, "切回后赛前文案恢复");
    }
}
