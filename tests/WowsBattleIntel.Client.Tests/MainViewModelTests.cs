using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Client.ViewModels;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Tests;

public class MainViewModelTests
{
    /// <summary>
    /// 轮询等待条件成立。详情加载是属性变更触发的后台任务，测试无法直接 await。
    /// </summary>
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

    private static BattleRecord MakeBattle(
        string battleId,
        BattleOutcome outcome,
        DateTimeOffset startedAt)
    {
        return new BattleRecord
        {
            BattleId = battleId,
            StartedAt = startedAt,
            MapId = "spaces/50_Gold_harbor",
            BattleType = BattleType.Random,
            GameVersion = "15.6.0",
            Server = GameServer.Asia,
            Outcome = outcome,
            DurationSeconds = 900,
            Players =
            [
                new PlayerRecord
                {
                    AccountId = "1001",
                    PlayerName = "AllyOne",
                    Server = GameServer.Asia,
                    IsAlly = true,
                    ShipName = "TestShip",
                    ShipTier = 10,
                    ShipClass = ShipClass.Battleship,
                    AccountWinrate = 0.56,
                    AccountBattles = 1000,
                    DamageDealt = 80000,
                },
                new PlayerRecord
                {
                    AccountId = "2001",
                    PlayerName = "EnemyOne",
                    Server = GameServer.Asia,
                    IsAlly = false,
                    ShipName = "TestShip2",
                    ShipTier = 10,
                    ShipClass = ShipClass.Cruiser,
                    AccountWinrate = 0.48,
                    AccountBattles = 900,
                },
            ],
        };
    }

    private static async Task SeedBattleAsync(
        AppServices services,
        string battleId,
        BattleOutcome outcome,
        double allyWinProbability)
    {
        var repository = services.Repository;
        await repository.SaveBattleAsync(MakeBattle(
            battleId, outcome, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await repository.SavePredictionAsync(new PredictionResult
        {
            BattleId = battleId,
            PredictedAt = DateTimeOffset.UtcNow,
            AllyWinProbability = allyWinProbability,
            Confidence = 0.75,
            ModelVersion = "baseline-logistic-v1",
        });
        await repository.BackfillOutcomeAsync(battleId, outcome);
    }

    [Fact]
    public async Task LoadRecentBattles_PopulatesListWithPrediction()
    {
        using var holder = await TestServices.CreateAsync();
        await SeedBattleAsync(holder.Services, "battle-001", BattleOutcome.Win, 0.68);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();

        var item = Assert.Single(vm.RecentBattles);
        Assert.Equal("battle-001", item.Battle.BattleId);
        Assert.Equal(0.68, item.PredictedWinProbability);
        Assert.Equal("胜", item.ResultLabel);
        Assert.Equal("预测 68%", item.PredictionText);
        Assert.Contains("亚服", item.SecondaryText);
    }

    [Fact]
    public async Task SelectBattle_LoadsRosterAndPrediction()
    {
        using var holder = await TestServices.CreateAsync();
        await SeedBattleAsync(holder.Services, "battle-001", BattleOutcome.Win, 0.68);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();

        vm.SelectedBattleSummary = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        Assert.Single(vm.Allies);
        Assert.Single(vm.Enemies);
        Assert.Equal("AllyOne", vm.Allies[0].PlayerName);
        Assert.Equal("EnemyOne", vm.Enemies[0].PlayerName);

        Assert.Equal("68.0%", vm.PredictedWinrateText);
        Assert.Equal("32.0%", vm.EnemyWinrateText);
        Assert.Equal("预测正确", vm.PredictionVerdict);
        Assert.Contains("亚服", vm.BattleSubtitle);
        Assert.Contains("15 分", vm.BattleSubtitle);
    }

    [Fact]
    public async Task SelectBattle_SplitsCoverageAndModelConfidence()
    {
        using var holder = await TestServices.CreateAsync();
        // 种子数据的 Confidence 为 0.75，模型置信度按 |0.68-0.5|*2 推导。
        await SeedBattleAsync(holder.Services, "battle-001", BattleOutcome.Win, 0.68);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedBattleSummary = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        // 覆盖率来自 Confidence，模型置信度由概率推导，两个数字必须分开。
        Assert.Equal("75%", vm.DataCoverageText);
        Assert.Equal("36%", vm.ModelConfidenceText);
        Assert.Equal(0.68, vm.AllyProbabilityRatio);
    }

    [Fact]
    public async Task SelectBattle_WithoutPrediction_MetricsShowDash()
    {
        using var holder = await TestServices.CreateAsync();
        await holder.Services.Repository.SaveBattleAsync(
            MakeBattle("battle-001", BattleOutcome.Win, DateTimeOffset.UtcNow));

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedBattleSummary = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        // 无预测时模型置信度显示占位符，概率条保持中线。
        Assert.Equal("-", vm.ModelConfidenceText);
        Assert.Equal("-", vm.DataCoverageText);
        Assert.Equal(0.5, vm.AllyProbabilityRatio);
    }

    [Fact]
    public async Task LoadRecentBattles_PartitionsLiveAndHistory()
    {
        using var holder = await TestServices.CreateAsync();
        // 一局在近三小时内，一局在三天前，分别落入实时与历史分区。
        await SeedBattleAsync(holder.Services, "battle-live", BattleOutcome.Win, 0.6);
        var stale = MakeBattle("battle-old", BattleOutcome.Loss, DateTimeOffset.UtcNow.AddDays(-3));
        await holder.Services.Repository.SaveBattleAsync(stale);
        await holder.Services.Repository.SavePredictionAsync(new PredictionResult
        {
            BattleId = "battle-old",
            PredictedAt = DateTimeOffset.UtcNow,
            AllyWinProbability = 0.4,
        });

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();

        var live = Assert.Single(vm.LiveBattles);
        Assert.Equal("battle-live", live.Battle.BattleId);
        Assert.True(live.IsLive);

        var history = Assert.Single(vm.HistoryBattles);
        Assert.Equal("battle-old", history.Battle.BattleId);
        Assert.False(history.IsLive);

        Assert.True(vm.HasLiveBattles);
        Assert.Equal("WinBrush", live.ResultBrushKey);
        Assert.Equal("LossBrush", history.ResultBrushKey);
    }

    [Fact]
    public async Task PredictionVerdict_WrongDirection_ReportsWrong()
    {
        using var holder = await TestServices.CreateAsync();
        // 预测友方胜率 0.3，实际友方获胜，方向判错。
        await SeedBattleAsync(holder.Services, "battle-001", BattleOutcome.Win, 0.3);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedBattleSummary = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        Assert.Equal("预测错误", vm.PredictionVerdict);
    }

    [Fact]
    public async Task SelectBattle_WithoutPrediction_ShowsPlaceholder()
    {
        using var holder = await TestServices.CreateAsync();
        await holder.Services.Repository.SaveBattleAsync(
            MakeBattle("battle-001", BattleOutcome.Win, DateTimeOffset.UtcNow));

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedBattleSummary = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        Assert.Equal("-", vm.PredictedWinrateText);
        Assert.Equal("待对照", vm.PredictionVerdict);
    }

    [Fact]
    public async Task StartAndStopObservation_TogglesFlag()
    {
        using var holder = await TestServices.CreateAsync();
        var vm = new MainViewModel(holder.Services);

        Assert.False(vm.IsObserving);
        Assert.Equal("开始观测", vm.ObservationButtonText);

        vm.StartObservation();
        Assert.True(vm.IsObserving);
        Assert.Equal("停止观测", vm.ObservationButtonText);
        Assert.Contains("观测中", vm.ObservationStatusText);

        vm.StopObservation();
        Assert.False(vm.IsObserving);
        Assert.Equal("未观测", vm.ObservationStatusText);
    }

    [Fact]
    public async Task Refresh_KeepsSelectedBattle()
    {
        using var holder = await TestServices.CreateAsync();
        await SeedBattleAsync(holder.Services, "battle-001", BattleOutcome.Win, 0.6);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedBattleSummary = vm.RecentBattles[0];

        await vm.LoadRecentBattlesAsync();

        Assert.NotNull(vm.SelectedBattleSummary);
        Assert.Equal("battle-001", vm.SelectedBattle!.BattleId);
    }

    [Fact]
    public async Task SelectBattle_ExtremeProbability_ClampsDisplayAndShowsTier()
    {
        using var holder = await TestServices.CreateAsync();
        // 入库 0.997 的极端概率，展示层必须夹到 95% 且不得出现 100%。
        await SeedBattleAsync(holder.Services, "battle-001", BattleOutcome.Win, 0.997);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedBattleSummary = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        Assert.Equal("95.0%", vm.PredictedWinrateText);
        Assert.Equal("5.0%", vm.EnemyWinrateText);
        Assert.Equal("明显优势", vm.TierText);
        Assert.Equal("WinBrush", vm.TierBrushKey);
        // 概率条保持原始值，截断只发生在展示读数。
        Assert.Equal(0.997, vm.AllyProbabilityRatio, precision: 10);
    }

    [Fact]
    public async Task SelectBattle_MidProbability_ShowsEvenTier()
    {
        using var holder = await TestServices.CreateAsync();
        await SeedBattleAsync(holder.Services, "battle-001", BattleOutcome.Win, 0.5);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedBattleSummary = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        Assert.Equal("势均力敌", vm.TierText);
        Assert.Equal("50.0%", vm.PredictedWinrateText);
    }
}
