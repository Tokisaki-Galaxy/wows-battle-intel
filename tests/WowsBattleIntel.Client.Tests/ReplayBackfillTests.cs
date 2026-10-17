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
        string replays = NewReplayDir();
        try
        {
            // 库里有一局，回放目录里另有一局从未入库。
            await SeedStoredBattleAsync(holder.Services, "battle-stored", DaysAgo(3));
            CreateReplay(replays, DaysAgo(1));

            var vm = new MainViewModel(holder.Services, replayDirectory: replays);
            await vm.LoadRecentBattlesAsync();

            Assert.Equal(2, vm.HistoryBattles.Count);
            var placeholder = Assert.Single(vm.HistoryBattles, s => s.IsPlaceholder);
            Assert.Equal("待解析", placeholder.PredictionText);
            Assert.Contains("点击解析", placeholder.SecondaryText);

            // 占位项尚未入库，库里的记录数没有变化。
            var stored = await holder.Services.Repository.GetBattleListAsync(10);
            Assert.Single(stored);
        }
        finally
        {
            Directory.Delete(replays, recursive: true);
        }
    }

    [Fact]
    public async Task LoadRecentBattles_StoredReplayIsNotOfferedAgain()
    {
        using var holder = await TestServices.CreateAsync();
        string replays = NewReplayDir();
        try
        {
            var startedAt = DaysAgo(2);
            await SeedStoredBattleAsync(holder.Services, "battle-stored", startedAt);
            CreateReplay(replays, startedAt);

            var vm = new MainViewModel(holder.Services, replayDirectory: replays);
            await vm.LoadRecentBattlesAsync();

            Assert.Single(vm.HistoryBattles);
            Assert.DoesNotContain(vm.HistoryBattles, s => s.IsPlaceholder);
        }
        finally
        {
            Directory.Delete(replays, recursive: true);
        }
    }

    [Fact]
    public async Task LoadRecentBattles_NewestPlaceholderSortsFirst()
    {
        using var holder = await TestServices.CreateAsync();
        string replays = NewReplayDir();
        try
        {
            await SeedStoredBattleAsync(holder.Services, "battle-stored", DaysAgo(5));
            CreateReplay(replays, DaysAgo(1));

            var vm = new MainViewModel(holder.Services, replayDirectory: replays);
            await vm.LoadRecentBattlesAsync();

            Assert.True(vm.RecentBattles[0].IsPlaceholder);
            Assert.Equal("battle-stored", vm.RecentBattles[1].BattleId);
        }
        finally
        {
            Directory.Delete(replays, recursive: true);
        }
    }

    [Fact]
    public async Task LoadRecentBattles_PrebattleRecordShowsAsPrebattleNotAsResult()
    {
        using var holder = await TestServices.CreateAsync();
        string replays = NewReplayDir();
        try
        {
            // 赛前记录没有战果，不能冒充成一场已有结果的对局。
            var pre = new BattleRecord
            {
                BattleId = "deadbeef:pre",
                StartedAt = DateTimeOffset.Now.AddHours(-1),
                BattleType = BattleType.Random,
                Outcome = BattleOutcome.Unknown,
            };
            await holder.Services.Repository.SaveBattleAsync(pre);

            var vm = new MainViewModel(holder.Services, replayDirectory: replays);
            await vm.LoadRecentBattlesAsync();

            var item = Assert.Single(vm.HistoryBattles);
            Assert.True(item.IsPrebattle);
            Assert.Equal("赛前", item.ResultLabel);
            Assert.Contains("赛前名单", item.SecondaryText);
        }
        finally
        {
            Directory.Delete(replays, recursive: true);
        }
    }

    [Fact]
    public async Task BuildSnapshot_ClearsPrebattlePlaceholderWhenFormalRecordExists()
    {
        using var holder = await TestServices.CreateAsync();
        string replays = NewReplayDir();
        try
        {
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

            var config = new AppConfig { GamePath = Path.GetDirectoryName(replays)! };
            var backfill = new ReplayBackfillService(
                () => config,
                holder.Services.Repository,
                holder.Services.Ingestion,
                () => holder.Services.Repository.GetStartedAtValuesAsync());

            var snapshot = await backfill.BuildSnapshotAsync(100);

            // 同一局只留正式记录，占位记录被清掉。
            Assert.DoesNotContain(snapshot.Battles, e => e.IsPrebattle);
            Assert.Contains(snapshot.Battles, e => e.BattleId == "formal");
            Assert.Empty(snapshot.PendingReplays);
        }
        finally
        {
            Directory.Delete(replays, recursive: true);
        }
    }

    [Fact]
    public async Task IsKnownAsync_MatchesByFilenameTimestamp()
    {
        using var holder = await TestServices.CreateAsync();
        var startedAt = DaysAgo(2);
        await SeedStoredBattleAsync(holder.Services, "battle-stored", startedAt);

        var config = new AppConfig();
        var backfill = new ReplayBackfillService(
            () => config,
            holder.Services.Repository,
            holder.Services.Ingestion,
            () => holder.Services.Repository.GetStartedAtValuesAsync());

        Assert.True(await backfill.IsKnownAsync(startedAt.AddSeconds(20)));
        Assert.False(await backfill.IsKnownAsync(startedAt.AddMinutes(30)));
    }

    [Theory]
    [InlineData(@"20261017_132514_PBSB110-Conqueror_22_tierra_del_fuego.wowsreplay", true)]
    [InlineData(@"D:\replays\15.9.0.0\20261017_132514_PBSB110-Conqueror_22_tierra_del_fuego.wowsreplay", true)]
    [InlineData("temp.wowsreplay", false)]
    [InlineData("random_name.wowsreplay", false)]
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
        string root = NewReplayDir();
        try
        {
            CreateReplay(root, DateTimeOffset.Now.AddDays(-1));
            // 临时文件与文件名不合规的文件都不算正式回放。
            File.WriteAllBytes(Path.Combine(root, ReplayDirectoryScanner.TemporaryFileName), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "notes.wowsreplay"), [1, 2, 3]);
            // 刚写入的文件还在不稳定窗口内。
            string fresh = Path.Combine(
                root,
                $"{DateTimeOffset.Now:yyyyMMdd_HHmmss}_fresh_ship_map.wowsreplay");
            File.WriteAllBytes(fresh, [1, 2, 3]);

            var stableOnly = ReplayDirectoryScanner.Scan(root, TimeSpan.FromSeconds(5));
            Assert.Single(stableOnly);

            var all = ReplayDirectoryScanner.Scan(root, TimeSpan.Zero);
            Assert.Equal(2, all.Count);
            Assert.Equal(fresh, all[0].Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DateTimeOffset DaysAgo(int days) =>
        DateTimeOffset.Now.AddDays(-days).AddSeconds(-30);

    private static string NewReplayDir()
    {
        string dir = Path.Combine(TestServices.TestTempRoot, $"wbi-replays-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>按开战时间造一份完整回放的替身文件。内容不重要，扫描只看文件名。</summary>
    private static string CreateReplay(string root, DateTimeOffset startedAt)
    {
        // 文件名的时间是本机本地时间。
        string name = $"{startedAt.ToLocalTime():yyyyMMdd_HHmmss}_PBSB110-Test_23_Shards.wowsreplay";
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTime(path, DateTime.Now.AddMinutes(-10));
        return path;
    }

    private static async Task SeedStoredBattleAsync(
        AppServices services,
        string battleId,
        DateTimeOffset startedAt)
    {
        var battle = new BattleRecord
        {
            BattleId = battleId,
            StartedAt = startedAt,
            MapId = "spaces/23_Shards",
            BattleType = BattleType.Random,
            Server = GameServer.Asia,
            Outcome = BattleOutcome.Win,
            DurationSeconds = 600,
        };
        await services.Repository.SaveBattleAsync(battle);
    }
}
