using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Client.ViewModels;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Tests;

public class MainViewModelTests
{
    /// <summary>
    /// 轮询等待条件成立。详情加载是属性变更触发的后台任务，测试无法直接 await。
    /// 涉及在线补拉的等待需要更长窗口：统计来源开启分舰种目录后，失败前
    /// 会先做十几次网络请求，默认 5 秒不够，此类场景传 timeout。
    /// </summary>
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
        Assert.Equal("胜利", item.ResultLabel);
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

        vm.SelectedHistoryBattle = vm.RecentBattles[0];
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
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        Assert.Equal("36%", vm.ModelConfidenceText);
    }

    [Fact]
    public async Task SelectBattle_WithoutPrediction_MetricsShowDash()
    {
        using var holder = await TestServices.CreateAsync();
        await holder.Services.Repository.SaveBattleAsync(
            MakeBattle("battle-001", BattleOutcome.Win, DateTimeOffset.UtcNow));

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        // 无预测时模型置信度显示占位符。
        Assert.Equal("-", vm.ModelConfidenceText);
    }

    [Fact]
    public async Task LoadRecentBattles_AllStoredBattlesAreHistory()
    {
        using var holder = await TestServices.CreateAsync();
        // 入库记录都属于历史；实时区只显示当前 tempArenaInfo 捕获的对局。
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

        // 没有当前对局时实时区为空。
        Assert.Empty(vm.LiveBattles);
        Assert.False(vm.HasLiveBattles);

        // 两局入库记录都进历史区。
        Assert.Equal(2, vm.HistoryBattles.Count);
        Assert.Contains(vm.HistoryBattles, s => s.Battle.BattleId == "battle-live");
        Assert.Contains(vm.HistoryBattles, s => s.Battle.BattleId == "battle-old");
    }

    [Fact]
    public async Task LoadRecentBattles_HistoryFiltersApply()
    {
        using var holder = await TestServices.CreateAsync();
        await SeedBattleAsync(holder.Services, "battle-fresh", BattleOutcome.Win, 0.6);
        var stale = MakeBattle("battle-old", BattleOutcome.Loss, DateTimeOffset.UtcNow.AddDays(-30));
        await holder.Services.Repository.SaveBattleAsync(stale);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();

        // 近 7 天筛选把 30 天前的记录排除。
        vm.HistoryDaysFilterIndex = 2;
        var history = Assert.Single(vm.HistoryBattles);
        Assert.Equal("battle-fresh", history.Battle.BattleId);
        Assert.Equal("WinBrush", history.ResultBrushKey);
    }

    [Fact]
    public async Task PredictionVerdict_WrongDirection_ReportsWrong()
    {
        using var holder = await TestServices.CreateAsync();
        // 预测友方胜率 0.3，实际友方获胜，方向判错。
        await SeedBattleAsync(holder.Services, "battle-001", BattleOutcome.Win, 0.3);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
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
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        Assert.Equal("-", vm.PredictedWinrateText);
        Assert.Equal("待对照", vm.PredictionVerdict);
    }

    [Fact]
    public async Task LoadRecentBattles_AttachesPlayersForTierLabel()
    {
        using var holder = await TestServices.CreateAsync();
        await SeedBattleAsync(holder.Services, "battle-tier", BattleOutcome.Win, 0.6);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();

        // 列表查询原本不带参战者，等级标签会退到 T?。补齐后应显示实际等级。
        var item = Assert.Single(vm.RecentBattles);
        Assert.Equal("T10", item.TierText);
    }

    [Fact]
    public void TierText_EmptyRoster_ShowsPlaceholder()
    {
        var battle = new BattleRecord
        {
            BattleId = "t",
            StartedAt = DateTimeOffset.UtcNow,
            Players = [],
        };
        var summary = BattleSummaryViewModel.FromBattle(battle, string.Empty);
        Assert.Equal("T?", summary.TierText);
    }

    [Fact]
    public async Task ObservationStatus_AlwaysReportsObserving()
    {
        using var holder = await TestServices.CreateAsync();
        var vm = new MainViewModel(holder.Services);

        // 观测常驻，界面不再提供开始/停止开关，状态栏始终反映观测中。
        Assert.Contains("观测中", vm.ObservationStatusText);
    }

    [Fact]
    public async Task Selection_LiveAndHistoryAreMutuallyExclusive()
    {
        using var holder = await TestServices.CreateAsync();
        await SeedBattleAsync(holder.Services, "battle-001", BattleOutcome.Win, 0.6);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "历史对局详情加载");

        // 切到实时区：历史区选中被清空，反之亦然。两个列表不能共用选中项，
        // 否则在其中一个列表选中会清掉另一个，表现为"点了历史就回不到当前对局"。
        var live = BattleSummaryViewModel.FromPreBattle(new BattleRecord
        {
            BattleId = "pre-1",
            StartedAt = DateTimeOffset.UtcNow,
        });
        vm.SelectedLiveBattle = live;

        Assert.Null(vm.SelectedHistoryBattle);
        Assert.Same(live, vm.SelectedLiveBattle);

        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        Assert.Null(vm.SelectedLiveBattle);
    }

    [Fact]
    public async Task Refresh_KeepsSelectedBattle()
    {
        using var holder = await TestServices.CreateAsync();
        await SeedBattleAsync(holder.Services, "battle-001", BattleOutcome.Win, 0.6);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedHistoryBattle = vm.RecentBattles[0];

        await vm.LoadRecentBattlesAsync();

        Assert.NotNull(vm.SelectedHistoryBattle);
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
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        Assert.Equal("95.0%", vm.PredictedWinrateText);
        Assert.Equal("5.0%", vm.EnemyWinrateText);
        Assert.Equal("明显优势", vm.TierText);
        Assert.Equal("WinBrush", vm.TierBrushKey);
    }

    [Fact]
    public async Task SelectBattle_MidProbability_ShowsEvenTier()
    {
        using var holder = await TestServices.CreateAsync();
        await SeedBattleAsync(holder.Services, "battle-001", BattleOutcome.Win, 0.5);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        Assert.Equal("势均力敌", vm.TierText);
        Assert.Equal("50.0%", vm.PredictedWinrateText);
    }

    [Fact]
    public async Task SelectBattle_SortsRosterByShipClassThenAccountId()
    {
        using var holder = await TestServices.CreateAsync();
        // 四种舰种乱序入库，账号标识故意与舰种顺序错开。
        var battle = new BattleRecord
        {
            BattleId = "battle-sort",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            MapId = "spaces/50_Gold_harbor",
            BattleType = BattleType.Random,
            GameVersion = "15.6.0",
            Server = GameServer.Asia,
            Outcome = BattleOutcome.Win,
            Players =
            [
                new PlayerRecord
                {
                    AccountId = "900", PlayerName = "DdPlayer", Server = GameServer.Asia,
                    IsAlly = true, ShipClass = ShipClass.Destroyer, ShipTier = 10,
                },
                new PlayerRecord
                {
                    AccountId = "100", PlayerName = "CvPlayer", Server = GameServer.Asia,
                    IsAlly = true, ShipClass = ShipClass.Carrier, ShipTier = 10,
                },
                new PlayerRecord
                {
                    AccountId = "500", PlayerName = "BbPlayerB", Server = GameServer.Asia,
                    IsAlly = true, ShipClass = ShipClass.Battleship, ShipTier = 10,
                },
                new PlayerRecord
                {
                    AccountId = "300", PlayerName = "BbPlayerA", Server = GameServer.Asia,
                    IsAlly = true, ShipClass = ShipClass.Battleship, ShipTier = 10,
                },
                new PlayerRecord
                {
                    AccountId = "700", PlayerName = "CaPlayer", Server = GameServer.Asia,
                    IsAlly = true, ShipClass = ShipClass.Cruiser, ShipTier = 10,
                },
            ],
        };
        await holder.Services.Repository.SaveBattleAsync(battle);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count == 5, "友方名单加载");

        // 舰种次序航母、战列、巡洋、驱逐，同舰种内按账号标识。
        Assert.Equal(
            ["CvPlayer", "BbPlayerA", "BbPlayerB", "CaPlayer", "DdPlayer"],
            vm.Allies.Select(p => p.PlayerName).ToArray());
    }

    [Fact]
    public async Task SelectBattle_SortsBotsAfterHumans()
    {
        using var holder = await TestServices.CreateAsync();
        // 真人整体先于人机，组内再按舰种、等级、账号标识。
        var battle = new BattleRecord
        {
            BattleId = "battle-bot-sort",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            MapId = "spaces/50_Gold_harbor",
            BattleType = BattleType.Random,
            GameVersion = "15.6.0",
            Server = GameServer.Asia,
            Outcome = BattleOutcome.Win,
            Players =
            [
                new PlayerRecord
                {
                    AccountId = "900", PlayerName = ":Tourville:", Server = GameServer.Asia,
                    IsAlly = true, IsBot = true, ShipClass = ShipClass.Battleship, ShipTier = 10,
                },
                new PlayerRecord
                {
                    AccountId = "100", PlayerName = "HumanDd", Server = GameServer.Asia,
                    IsAlly = true, ShipClass = ShipClass.Destroyer, ShipTier = 10,
                },
                new PlayerRecord
                {
                    AccountId = "200", PlayerName = ":Beatty:", Server = GameServer.Asia,
                    IsAlly = true, IsBot = true, ShipClass = ShipClass.Carrier, ShipTier = 10,
                },
                new PlayerRecord
                {
                    AccountId = "50", PlayerName = "HumanBb", Server = GameServer.Asia,
                    IsAlly = true, ShipClass = ShipClass.Battleship, ShipTier = 10,
                },
            ],
        };
        await holder.Services.Repository.SaveBattleAsync(battle);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count == 4, "友方名单加载");

        // 真人按舰种次序（BB 在 DD 前），人机整体垫底。
        Assert.Equal(
            ["HumanBb", "HumanDd", ":Beatty:", ":Tourville:"],
            vm.Allies.Select(p => p.PlayerName).ToArray());
    }

    [Fact]
    public async Task SelectBattle_TeamSummaryShowsAccountWeightedAndBattles()
    {
        using var holder = await TestServices.CreateAsync();
        var battle = MakeBattle("battle-summary", BattleOutcome.Win, DateTimeOffset.UtcNow.AddMinutes(-1));
        await holder.Services.Repository.SaveBattleAsync(battle);

        // 种一份快照。展示层取最新快照，不设对局时间上界，种在对局之后也生效。
        await holder.Services.Repository.SaveSnapshotAsync(new PlayerSnapshot
        {
            AccountId = "1001",
            Server = GameServer.Asia,
            CapturedAt = DateTimeOffset.UtcNow,
            AccountBattles = 1000,
            AccountWinrate = 0.56,
            WeightedWinrate = 0.5,
        });

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0, "友方名单加载");

        // 友方有快照：账号胜率、加权胜率与场次进汇总；敌方无快照显示占位符。
        Assert.Equal("账号: 56.00%  加权: 50.00%  场次: 1000.0", vm.AllySummaryText);
        Assert.Equal("-", vm.EnemySummaryText);
    }

    [Fact]
    public void BuildTeamSummary_CoversAccountWeightedAndBattles()
    {
        // 汇总口径直接验证：账号胜率、加权胜率、平均场次三项各取有样本的玩家。
        var players = new List<PlayerRecord>
        {
            new()
            {
                AccountId = "1",
                AccountWinrate = 0.56,
                WeightedWinrate = 0.50,
                AccountBattles = 1000,
            },
            new()
            {
                AccountId = "2",
                AccountWinrate = 0.48,
                AccountBattles = 2000,
                // 无加权胜率数据，加权项只统计第一个玩家。
            },
        };

        Assert.Equal("账号: 52.00%  加权: 50.00%  场次: 1500.0", MainViewModel.BuildTeamSummary(players));
        Assert.Equal("-", MainViewModel.BuildTeamSummary([new PlayerRecord { AccountId = "3" }]));
    }

    [Fact]
    public void SelectPlayersNeedingStats_IncludesSentinelAccountPlayers()
    {
        // 赛前名单的账号 ID 是 -1 哨兵，真账号要由补拉按昵称解析。
        // 若这里按 -1 过滤，整份赛前名单会被滤掉，界面恒显示"隐藏"。
        var sentinel = new PlayerRecord
        {
            AccountId = "-1",
            PlayerName = "PreBattlePlayer",
            AccountBattles = -1,
            ShipBattles = -1,
        };
        var bot = new PlayerRecord
        {
            AccountId = "-1",
            PlayerName = ":bot",
            IsBot = true,
            AccountBattles = -1,
        };
        var alreadyCovered = new PlayerRecord
        {
            AccountId = "2002",
            PlayerName = "Covered",
            AccountWinrate = 0.55,
            AccountBattles = 1000,
            ShipBattles = 100,
            ClassStatsJson = "{\"BB\":{\"battles\":10,\"wins\":5}}",
        };

        var result = MainViewModel.SelectPlayersNeedingStats([sentinel, bot, alreadyCovered]).ToList();

        // 哨兵玩家必须入选，人机与三项数据齐全的玩家排除。
        var only = Assert.Single(result);
        Assert.Same(sentinel, only);
    }

    [Fact]
    public void SelectPlayersNeedingStats_IncompleteClassOrShipStats_StillIncluded()
    {
        // 只到账号胜率不算齐全：舰种或单船数据缺了要继续拉，
        // 否则表格的舰种列与单船列会一直空着。
        var missingClass = new PlayerRecord
        {
            AccountId = "3001",
            PlayerName = "NoClass",
            AccountWinrate = 0.5,
            ShipBattles = 100,
            ClassStatsJson = string.Empty,
        };
        var missingShip = new PlayerRecord
        {
            AccountId = "3002",
            PlayerName = "NoShip",
            AccountWinrate = 0.5,
            ShipBattles = -1,
            ClassStatsJson = "{\"BB\":{\"battles\":10,\"wins\":5}}",
        };
        var emptyClass = new PlayerRecord
        {
            AccountId = "3003",
            PlayerName = "EmptyClass",
            AccountWinrate = 0.5,
            ShipBattles = 100,
            ClassStatsJson = "{}",
        };

        var result = MainViewModel
            .SelectPlayersNeedingStats([missingClass, missingShip, emptyClass])
            .ToList();

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void SelectPlayersNeedingStats_HiddenPlayerExcluded()
    {
        // 隐藏战绩玩家 WG API 永不返回数据，反复拉只会空转。
        var hidden = new PlayerRecord
        {
            AccountId = "4001",
            PlayerName = "Hidden",
            IsHidden = true,
            AccountWinrate = -1,
        };

        Assert.Empty(MainViewModel.SelectPlayersNeedingStats([hidden]));
    }

    [Fact]
    public void DescribeCoverage_SeparatesHiddenAndIncomplete()
    {
        // 隐藏战绩不计入不完整，否则收敛循环永不结束。
        var battle = new BattleRecord
        {
            BattleId = "cov",
            StartedAt = DateTimeOffset.UtcNow,
            Players =
            [
                new PlayerRecord
                {
                    AccountId = "1", PlayerName = "Full", AccountWinrate = 0.5,
                    ShipBattles = 10, ClassStatsJson = "{\"BB\":{\"battles\":1,\"wins\":0}}",
                },
                new PlayerRecord
                {
                    AccountId = "2", PlayerName = "Partial", AccountWinrate = 0.5,
                    ShipBattles = -1, ClassStatsJson = string.Empty,
                },
                new PlayerRecord
                {
                    AccountId = "3", PlayerName = "Hidden", IsHidden = true, AccountWinrate = -1,
                },
            ],
        };

        var (humans, covered, hidden, incomplete) = MainViewModel.DescribeCoverage(battle);

        Assert.Equal(3, humans);
        Assert.Equal(1, covered);
        Assert.Equal(1, hidden);
        Assert.Equal(1, incomplete);
    }

    [Fact]
    public void SelectPlayersNeedingStats_EmptyNameExcluded()
    {
        // 空昵称无法按昵称解析账号，排除。
        var empty = new PlayerRecord
        {
            AccountId = "-1",
            PlayerName = string.Empty,
            AccountBattles = -1,
        };

        Assert.Empty(MainViewModel.SelectPlayersNeedingStats([empty]));
    }

    [Fact]
    public async Task SelectBattle_ProgressiveLoad_RosterFirstThenBackgroundStatsFetch()
    {
        // 渐进式加载路径：缺统计的名单先展示占位行，补拉失败后保留占位符，
        // 界面不等在线拉取，也不因补拉失败丢失名单。
        using var holder = await TestServices.CreateAsync();
        var battle = MakeBattle("battle-progressive", BattleOutcome.Win, DateTimeOffset.UtcNow.AddMinutes(-1));
        // 抹掉种子数据的统计，制造快照缺失的占位行。
        foreach (var p in battle.Players)
        {
            p.AccountWinrate = -1;
            p.AccountBattles = -1;
        }

        await holder.Services.Repository.SaveBattleAsync(battle);

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();
        vm.SelectedHistoryBattle = vm.RecentBattles[0];

        // 阶段一立即出现名单，不等在线补拉。
        await WaitUntilAsync(
            () => vm.Allies.Count > 0 && vm.Enemies.Count > 0 && !vm.IsBusy,
            "阶段一名单加载");
        Assert.Equal("-", vm.Allies[0].BattlesText);
        Assert.Equal("隐藏", vm.Allies[0].WinrateText);
        Assert.Equal("-", vm.Allies[0].WeightedWinrateText);

        // 阶段二在后台拉取，测试环境网络必败，占位符保留为合法终态。
        // 补拉失败前会先尝试分舰种目录的十几次网络请求，窗口放宽到 60 秒：
        // 有代理的环境下请求从秒拒变慢速超时，30 秒不够。
        await WaitUntilAsync(
            () => !vm.StatusMessage.StartsWith("正在补充", StringComparison.Ordinal),
            "阶段二补拉结束",
            timeoutSeconds: 60);
        Assert.Equal("-", vm.Allies[0].BattlesText);
        Assert.Single(vm.Allies);
        Assert.Single(vm.Enemies);
    }

    [Fact]
    public async Task SelectBattle_ProgressiveLoad_StatusClearedOnNextSelection()
    {
        // 换局时上一局的补拉进度提示必须清掉，不留旧进度。
        using var holder = await TestServices.CreateAsync();
        // 第一局缺统计，触发补拉提示；第二局统计齐全，不开启补拉。
        var first = MakeBattle("battle-a", BattleOutcome.Win, DateTimeOffset.UtcNow.AddMinutes(-1));
        foreach (var p in first.Players)
        {
            p.AccountWinrate = -1;
            p.AccountBattles = -1;
        }

        await holder.Services.Repository.SaveBattleAsync(first);
        var second = MakeBattle("battle-b", BattleOutcome.Win, DateTimeOffset.UtcNow.AddMinutes(-2));
        second.Players[0].ShipId = "SHIP-A";
        second.Players[1].ShipId = "SHIP-B";
        await holder.Services.Repository.SaveBattleAsync(second);

        // battle_players 不保存战绩字段，第二局不触发补拉要靠快照命中，
        // 快照带匹配的 ship_id 才能一并补上单船场次。
        var seedShips = new Dictionary<string, string>
        {
            ["1001"] = "SHIP-A",
            ["2001"] = "SHIP-B",
        };
        foreach (var (accountId, shipId) in seedShips)
        {
            await holder.Services.Repository.SaveSnapshotAsync(new PlayerSnapshot
            {
                AccountId = accountId,
                Server = GameServer.Asia,
                CapturedAt = DateTimeOffset.UtcNow,
                AccountBattles = 1000,
                AccountWinrate = 0.5,
                WeightedWinrate = 0.5,
                ShipId = shipId,
                ShipBattles = 100,
                // 分舰种数据也要齐，否则按新口径仍算不完整会触发补拉。
                ClassStatsJson = "{\"BB\":{\"battles\":100,\"wins\":50}}",
            });
        }

        var vm = new MainViewModel(holder.Services);
        await vm.LoadRecentBattlesAsync();

        vm.SelectedHistoryBattle = vm.RecentBattles[0];
        await WaitUntilAsync(() => vm.Allies.Count > 0 && !vm.IsBusy, "第一局加载");
        await WaitUntilAsync(
            () => vm.StatusMessage.StartsWith("正在补充", StringComparison.Ordinal),
            "补拉提示出现");

        vm.SelectedHistoryBattle = vm.RecentBattles[1];
        await WaitUntilAsync(
            () => vm.Allies.Count > 0 && !vm.IsBusy,
            "第二局加载");
        Assert.False(vm.StatusMessage.StartsWith("正在补充", StringComparison.Ordinal));
    }
}
