using System.IO;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Client.ViewModels;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 历史回放补录链路：回放目录里未入库的对局以占位项列出，选中时才解析。
/// </summary>
public class ReplayBackfillTests
{
    [Fact]
    public async Task LoadRecentBattles_ListsUnstoredReplayAsPlaceholder()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        // 库里有一局，回放目录里另有一局从未入库。
        await SeedStoredBattleAsync(holder.Services, "battle-stored", DaysAgo(3));
        replays.Create(DaysAgo(1));

        var vm = CreateViewModel(holder.Services, replays.Path);
        await vm.LoadRecentBattlesAsync();

        Assert.Equal(2, vm.HistoryBattles.Count);
        var placeholder = Assert.Single(vm.HistoryBattles, s => s.IsPlaceholder);
        Assert.Equal("待解析", placeholder.PredictionText);
        Assert.Contains("点击解析", placeholder.SecondaryText);

        // 占位项尚未入库，库里的记录数没有变化。
        var stored = await holder.Services.Repository.GetBattleListAsync(10);
        Assert.Single(stored);
    }

    [Fact]
    public async Task LoadRecentBattles_StoredReplayIsNotOfferedAgain()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        var startedAt = DaysAgo(2);
        await SeedStoredBattleAsync(holder.Services, "battle-stored", startedAt);
        replays.Create(startedAt);

        var vm = CreateViewModel(holder.Services, replays.Path);
        await vm.LoadRecentBattlesAsync();

        Assert.Single(vm.HistoryBattles);
        Assert.DoesNotContain(vm.HistoryBattles, s => s.IsPlaceholder);
    }

    [Fact]
    public async Task LoadRecentBattles_NewestPlaceholderSortsFirst()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        await SeedStoredBattleAsync(holder.Services, "battle-stored", DaysAgo(5));
        replays.Create(DaysAgo(1));

        var vm = CreateViewModel(holder.Services, replays.Path);
        await vm.LoadRecentBattlesAsync();

        Assert.True(vm.RecentBattles[0].IsPlaceholder);
        Assert.Equal("battle-stored", vm.RecentBattles[1].BattleId);
    }

    [Fact]
    public async Task LoadRecentBattles_PrebattleRecordShowsAsPrebattleNotAsResult()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        // 赛前记录没有战果，不能冒充成一场已有结果的对局。
        var pre = new BattleRecord
        {
            BattleId = "deadbeef:pre",
            StartedAt = DateTimeOffset.Now.AddHours(-1),
            BattleType = BattleType.Random,
            Outcome = BattleOutcome.Unknown,
        };
        await holder.Services.Repository.SaveBattleAsync(pre);

        var vm = CreateViewModel(holder.Services, replays.Path);
        await vm.LoadRecentBattlesAsync();

        var item = Assert.Single(vm.HistoryBattles);
        Assert.True(item.IsPrebattle);
        Assert.Equal("赛前", item.ResultLabel);
        Assert.Contains("赛前名单", item.SecondaryText);
    }

    [Fact]
    public async Task BuildSnapshot_ClearsPrebattlePlaceholderWhenFormalRecordExists()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        var startedAt = DaysAgo(1);
        var pre = new BattleRecord
        {
            BattleId = "dup:pre",
            StartedAt = startedAt,
            BattleType = BattleType.Random,
            Outcome = BattleOutcome.Unknown,
        };
        await holder.Services.Repository.SaveBattleAsync(pre);
        await SeedStoredBattleAsync(holder.Services, "formal", startedAt.AddMinutes(1));

        var backfill = CreateEmptyBackfill(holder.Services, replays.Path);
        var snapshot = await backfill.BuildSnapshotAsync(100);

        // 同一局只留正式记录，占位记录被清掉。
        Assert.DoesNotContain(snapshot.Battles, e => e.IsPrebattle);
        Assert.Contains(snapshot.Battles, e => e.BattleId == "formal");
        Assert.Empty(snapshot.PendingReplays);

        // 占位记录连同它的预测一并移走，库里不再有 :pre 记录。
        Assert.Empty(await holder.Services.Repository.GetPrebattlePlaceholdersAsync());
    }

    [Fact]
    public async Task BuildSnapshot_ClearsPlaceholderEvenIfMapDiffers()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        // 占位记录带着另一张地图，正式记录也已入库。
        // 赛前名单的地图口径与回放解析结果可能不一致，不能拿地图当校验。
        var startedAt = DaysAgo(1);
        var pre = new BattleRecord
        {
            BattleId = "other:pre",
            StartedAt = startedAt,
            MapId = "spaces/14_Atlantic",
            BattleType = BattleType.Random,
            Outcome = BattleOutcome.Unknown,
        };
        await holder.Services.Repository.SaveBattleAsync(pre);
        await SeedStoredBattleAsync(holder.Services, "formal", startedAt.AddSeconds(15), mapId: "spaces/09_Other");

        var backfill = CreateEmptyBackfill(holder.Services, replays.Path);
        var snapshot = await backfill.BuildSnapshotAsync(100);

        Assert.DoesNotContain(snapshot.Battles, e => e.BattleId == "other:pre");
    }

    [Fact]
    public async Task BuildSnapshot_OldReplayBeyondListLimitIsNotOfferedAgain()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        // 库里的记录比列表上限更早，列表页取不到，但比对必须仍然认为它已入库。
        await SeedStoredBattleAsync(holder.Services, "ancient", DaysAgo(400));
        replays.Create(DaysAgo(400));

        var backfill = CreateEmptyBackfill(holder.Services, replays.Path);
        var snapshot = await backfill.BuildSnapshotAsync(limit: 1);

        Assert.Empty(snapshot.PendingReplays);
    }

    [Fact]
    public async Task IsKnownAsync_MatchesByFilenameTimestamp()
    {
        using var holder = await TestServices.CreateAsync();
        var startedAt = DaysAgo(2);
        await SeedStoredBattleAsync(holder.Services, "battle-stored", startedAt);

        var backfill = CreateEmptyBackfill(holder.Services, Path.GetTempPath());

        Assert.True(await backfill.IsKnownAsync(startedAt.AddSeconds(20)));
        Assert.False(await backfill.IsKnownAsync(startedAt.AddMinutes(30)));
    }

    [Fact]
    public async Task IngestAsync_AfterParsingPlaceholder_BecomesStoredEntry()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        // 这里只验证原样搬运：解析本身依赖游戏目录，不在单元测试里跑。
        var backfill = CreateEmptyBackfill(holder.Services, replays.Path);
        var missing = await backfill.IngestAsync(Path.Combine(replays.Path, "not-there.wowsreplay"));

        Assert.False(missing.Success);
        Assert.Contains("不存在", missing.Message);

        // 失败后不影响列表继续列出占位项。
        replays.Create(DaysAgo(1));
        var snapshot = await backfill.BuildSnapshotAsync(100);
        Assert.Single(snapshot.PendingReplays);
    }

    [Fact]
    public async Task SelectingStoredBattle_CancelsPendingPlaceholderParse()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        // 库里有一局可读的正式记录，目录里另有一局解析会失败的占位项。
        await SeedStoredBattleAsync(holder.Services, "battle-stored", DaysAgo(3));
        replays.Create(DaysAgo(1));

        var vm = CreateViewModel(holder.Services, replays.Path);
        await vm.LoadRecentBattlesAsync();

        var placeholder = vm.RecentBattles.First(s => s.IsPlaceholder);
        var stored = vm.RecentBattles.First(s => !s.IsPlaceholder);

        // 先点占位项（解析注定失败），立刻改点已入库的一局。
        vm.SelectedHistoryBattle = placeholder;
        vm.SelectedHistoryBattle = stored;

        await WaitUntilAsync(
            () => vm.SelectedBattle is not null && vm.SelectedBattle.BattleId == "battle-stored",
            5000);

        // 旧解析失败后不得改掉详情区，也不得把忙状态留在界面上。
        Assert.True(vm.SelectedBattle?.BattleId == "battle-stored");
        Assert.False(vm.IsBusy);
    }
    [Fact]
    public async Task BuildSnapshot_ClearsPlaceholderBeyondListLimit()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        // 占位记录很老，不在列表页内；对应的正式记录与它同一时间，也不在页内。
        // 清理必须覆盖全库，否则这条占位记录永远清不掉。
        var startedAt = DaysAgo(400);
        var pre = new BattleRecord
        {
            BattleId = "ancient:pre",
            StartedAt = startedAt,
            BattleType = BattleType.Random,
            Outcome = BattleOutcome.Unknown,
        };
        await holder.Services.Repository.SaveBattleAsync(pre);
        await SeedStoredBattleAsync(holder.Services, "ancient-formal", startedAt.AddSeconds(12));
        await SeedStoredBattleAsync(holder.Services, "recent", DaysAgo(1));

        var backfill = CreateEmptyBackfill(holder.Services, replays.Path);
        var snapshot = await backfill.BuildSnapshotAsync(limit: 1);

        Assert.DoesNotContain(snapshot.Battles, e => e.BattleId == "ancient:pre");
        Assert.Empty(await holder.Services.Repository.GetPrebattlePlaceholdersAsync());
    }

    [Fact]
    public async Task Remember_AfterIngestion_HidesReplayFromPendingList()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        var startedAt = DaysAgo(1);
        replays.Create(startedAt);

        var backfill = CreateEmptyBackfill(holder.Services, replays.Path);
        var before = await backfill.BuildSnapshotAsync(100);
        Assert.Single(before.PendingReplays);

        // 观测器实时入库后记进已知时间，下次快照不该再把它列成占位项。
        backfill.RememberIngested(startedAt);
        var after = await backfill.BuildSnapshotAsync(100);
        Assert.Empty(after.PendingReplays);
    }
    [Fact]
    public async Task BuildSnapshot_KeepsPlaceholderWhenFormalRecordIsOlder()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        // 时间窗里有一条更早的记录，但它不可能是这条占位记录的正式版本。
        var startedAt = DaysAgo(1);
        var pre = new BattleRecord
        {
            BattleId = "later:pre",
            StartedAt = startedAt,
            BattleType = BattleType.Random,
            Outcome = BattleOutcome.Unknown,
        };
        await holder.Services.Repository.SaveBattleAsync(pre);
        await SeedStoredBattleAsync(holder.Services, "earlier", startedAt.AddMinutes(-3));

        var backfill = CreateEmptyBackfill(holder.Services, replays.Path);
        var snapshot = await backfill.BuildSnapshotAsync(100);

        Assert.Contains(snapshot.Battles, e => e.BattleId == "later:pre");
    }
    [Fact]
    public async Task BuildSnapshot_ClearsPlaceholderWhenFormalSharesExactTimestamp()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        // 正式记录与占位记录同一时刻：必须算作已合并，不能因为"时间差为零"漏掉。
        var startedAt = DaysAgo(1);
        var pre = new BattleRecord
        {
            BattleId = "same:pre",
            StartedAt = startedAt,
            BattleType = BattleType.Random,
            Outcome = BattleOutcome.Unknown,
        };
        await holder.Services.Repository.SaveBattleAsync(pre);
        await SeedStoredBattleAsync(holder.Services, "same-formal", startedAt);

        var backfill = CreateEmptyBackfill(holder.Services, replays.Path);
        var snapshot = await backfill.BuildSnapshotAsync(100);

        Assert.DoesNotContain(snapshot.Battles, e => e.BattleId == "same:pre");
        Assert.Empty(await holder.Services.Repository.GetPrebattlePlaceholdersAsync());
    }

    [Fact]
    public async Task BuildSnapshot_KeepsLonePlaceholderWithoutFormalRecord()
    {
        using var holder = await TestServices.CreateAsync();
        using var replays = new TempReplayDir();

        // 库里只有占位记录、没有任何正式记录：不能清。占位记录自己的时间
        // 不能当作"已有正式记录"的证据。
        var startedAt = DaysAgo(1);
        var pre = new BattleRecord
        {
            BattleId = "lonely:pre",
            StartedAt = startedAt,
            BattleType = BattleType.Random,
            Outcome = BattleOutcome.Unknown,
        };
        await holder.Services.Repository.SaveBattleAsync(pre);

        var backfill = CreateEmptyBackfill(holder.Services, replays.Path);
        var snapshot = await backfill.BuildSnapshotAsync(100);

        Assert.Contains(snapshot.Battles, e => e.BattleId == "lonely:pre");
        Assert.Single(await holder.Services.Repository.GetPrebattlePlaceholdersAsync());
    }
    [Theory]
    [InlineData(@"20261017_132514_PBSB110-Conqueror_22_tierra_del_fuego.wowsreplay", true)]
    [InlineData(@"D:\replays\15.9.0.0\20261017_132514_PBSB110-Conqueror_22_tierra_del_fuego.wowsreplay", true)]
    [InlineData("temp.wowsreplay", false)]
    [InlineData("random_name.wowsreplay", false)]
    [InlineData("20261017_132514.wowsreplay", false)]
    [InlineData("20261017_132514X_ship.wowsreplay", false)]
    [InlineData(null, false)]
    public void ReplayFileName_ParsesTimestampFromName(string? name, bool expected)
    {
        bool ok = ReplayFileName.TryParseTimestamp(name, out var startedAt);

        Assert.Equal(expected, ok);
        if (expected)
        {
            Assert.Equal(2026, startedAt.Year);
            Assert.Equal(10, startedAt.Month);
            Assert.Equal(17, startedAt.Day);
            Assert.Equal(13, startedAt.Hour);
            Assert.Equal(25, startedAt.Minute);
        }
    }

    [Fact]
    public void ReplayDirectoryScanner_SkipsTempAndUnstableFiles()
    {
        using var replays = new TempReplayDir();

        replays.Create(DaysAgo(1));
        // 临时文件与文件名不合规的文件都不算正式回放。
        File.WriteAllBytes(Path.Combine(replays.Path, ReplayDirectoryScanner.TemporaryFileName), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(replays.Path, "notes.wowsreplay"), [1, 2, 3]);

        // 刚写入的文件还在不稳定窗口内。
        string fresh = Path.Combine(
            replays.Path,
            $"{DateTimeOffset.Now:yyyyMMdd_HHmmss}_fresh_ship_map.wowsreplay");
        File.WriteAllBytes(fresh, [1, 2, 3]);

        var stableOnly = ReplayDirectoryScanner.Scan(replays.Path, TimeSpan.FromSeconds(5));
        Assert.Single(stableOnly);

        var all = ReplayDirectoryScanner.Scan(replays.Path, TimeSpan.Zero);
        Assert.Equal(2, all.Count);
        Assert.Equal(fresh, all[0].Path);
    }

    [Fact]
    public async Task ReplayObserver_FirstScanReportsUnknownReplays()
    {
        using var replays = new TempReplayDir();
        // 回放目录是 <游戏目录>\replays，观测器读的是这个组合路径。
        var config = new AppConfig
        {
            GamePath = replays.GameDirectory,
            PollIntervalSeconds = 3,
        };
        replays.Create(DaysAgo(1));

        var seen = new List<string>();
        using var observer = new ReplayObserver(
            config,
            (path, _) => Task.FromResult(false));
        observer.ReplayDiscovered += path => seen.Add(path);

        observer.Start();
        await WaitUntilAsync(() => seen.Count == 1);
        observer.Stop();

        Assert.Single(seen);
    }

    [Fact]
    public async Task ReplayObserver_RebindModeFirstScanDoesNotReport()
    {
        using var replays = new TempReplayDir();
        // 回放目录是 <游戏目录>\replays，观测器读的是这个组合路径。
        var config = new AppConfig
        {
            GamePath = replays.GameDirectory,
            PollIntervalSeconds = 3,
        };
        replays.Create(DaysAgo(1));

        var seen = new List<string>();
        using var observer = new ReplayObserver(
            config,
            (path, _) => Task.FromResult(false),
            reportOnFirstScan: false);
        observer.ReplayDiscovered += path => seen.Add(path);

        observer.Start();

        // 首扫只登记不通知：设置页换绑后不该把目录里全部历史回放重新入队。
        await Task.Delay(600);
        observer.Stop();
        Assert.Empty(seen);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("等待目录首次扫描上报超时");
    }

    private static MainViewModel CreateViewModel(AppServices services, string replayRoot) =>
        new(services, null, CreateEmptyBackfill(services, replayRoot));

    /// <summary>
    /// 构造指向空回放目录的补录服务。列表只反映库里的记录，测试不依赖
    /// 开发机上的回放文件。
    /// </summary>
    internal static ReplayBackfillService CreateEmptyBackfill(AppServices services) =>
        CreateEmptyBackfill(services, string.Empty);

    private static ReplayBackfillService CreateEmptyBackfill(AppServices services, string replayRoot) =>
        new(
            () => new AppConfig { GamePath = replayRoot },
            services.Repository,
            services.Ingestion,
            () => services.Repository.GetStartedAtValuesAsync(),
            services.Logs,
            rootOverride: replayRoot);

    private static DateTimeOffset DaysAgo(int days) =>
        DateTimeOffset.Now.AddDays(-days).AddSeconds(-30);

    private static async Task SeedStoredBattleAsync(
        AppServices services,
        string battleId,
        DateTimeOffset startedAt,
        string mapId = "spaces/23_Shards")
    {
        var battle = new BattleRecord
        {
            BattleId = battleId,
            StartedAt = startedAt,
            MapId = mapId,
            BattleType = BattleType.Random,
            Server = GameServer.Asia,
            Outcome = BattleOutcome.Win,
            DurationSeconds = 600,
        };
        await services.Repository.SaveBattleAsync(battle);
    }

    /// <summary>
    /// 临时回放目录。按开战时间造空文件，扫描只看文件名与写入时间。
    /// </summary>
    private sealed class TempReplayDir : IDisposable
    {
        public TempReplayDir()
        {
            // 观测器读的是 <游戏目录>\replays，这里照这个结构建目录，
            // 避免把游戏目录指到测试根目录后和别的测试共用 replays。
            GameDirectory = System.IO.Path.Combine(
                TestServices.TestTempRoot,
                $"wbi-replays-{Guid.NewGuid():N}");
            Path = System.IO.Path.Combine(GameDirectory, "replays");
            Directory.CreateDirectory(Path);
        }

        /// <summary>临时游戏目录，其下的 replays 子目录是回放目录。</summary>
        public string GameDirectory { get; }

        /// <summary>回放目录。</summary>
        public string Path { get; }

        /// <summary>按开战时间落一份回放文件，写入时间拨回到过去以满足稳定判定。</summary>
        public string Create(DateTimeOffset startedAt)
        {
            string name = $"{startedAt.ToLocalTime():yyyyMMdd_HHmmss}_PBSB110-Test_23_Shards.wowsreplay";
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            // 相对 UTC 守卫判定，用本机时间会因时区偏差落到“仍在写入”一侧。
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(GameDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 测试收尾失败不影响断言结果。
            }
        }
    }
}
