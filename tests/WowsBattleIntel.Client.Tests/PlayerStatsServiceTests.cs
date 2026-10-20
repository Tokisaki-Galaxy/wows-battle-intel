using System.IO;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Stats;
using WowsBattleIntel.Storage;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 战绩统一入口与缓存装饰器的测试。锁住"拉到即落库、分块推进、取消不丢已完成块、
/// 缓存命中跳过网络"这几条。
/// </summary>
public class PlayerStatsServiceTests : IDisposable
{
    private readonly string databasePath;
    private readonly SqliteBattleRepository repository;

    public PlayerStatsServiceTests()
    {
        databasePath = TestServices.NewDatabasePath();
        repository = new SqliteBattleRepository(databasePath);
        repository.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(databasePath + suffix);
            }
            catch
            {
                // 清理失败不影响结论。
            }
        }
    }

    private static PlayerRecord MakePlayer(string name, string accountId) => new()
    {
        AccountId = accountId,
        PlayerName = name,
        Server = GameServer.Asia,
        AccountBattles = 1000,
        AccountWinrate = 0.55,
        ShipId = "4286581900",
        ShipBattles = 100,
        ShipWinrate = 0.6,
    };

    /// <summary>
    /// 假的统计来源：按调用次数给玩家补上账号 ID 与统计，可选地在第 N 批抛错。
    /// </summary>
    private sealed class FakeProvider : IPlayerStatsProvider
    {
        public string Name => "FAKE";

        /// <summary>第几次调用开始抛异常。0 表示从不抛。</summary>
        public int FailFromCall { get; set; }

        /// <summary>是否给分舰种字段赋值。默认不赋值以保持最小数据。</summary>
        public bool FillClassStats { get; set; }

        /// <summary>模拟 WG 判定隐藏战绩：跳过统计补全，与真实提供商一致。</summary>
        public bool MarkHidden { get; set; }

        /// <summary>给玩家补的军团标签，空表示不补。</summary>
        public string ClanTag { get; set; } = string.Empty;

        public int Calls { get; private set; }

        public Task<IReadOnlyList<PlayerRecord>> FetchStatsAsync(
            IReadOnlyList<PlayerRecord> players,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (FailFromCall > 0 && Calls >= FailFromCall)
            {
                throw new InvalidOperationException("模拟拉取失败");
            }

            foreach (var p in players)
            {
                // 模拟按昵称解析出真账号：哨兵先换成真 ID，再补统计。
                if (p.AccountId == "-1")
                {
                    p.AccountId = "2" + Math.Abs(p.PlayerName.GetHashCode() % 1000000);
                }

                if (MarkHidden)
                {
                    p.IsHidden = true;
                }
                else
                {
                    p.AccountBattles = 1000;
                    p.AccountWinrate = 0.55;
                    p.ShipBattles = 100;
                    p.ShipWinrate = 0.6;
                }

                if (FillClassStats)
                {
                    p.ClassStatsJson = "{\"CA\":{\"battles\":100,\"wins\":55,\"damage\":50000}}";
                }

                if (ClanTag.Length > 0 && p.ClanTag.Length == 0)
                {
                    p.ClanTag = ClanTag;
                }
            }

            return Task.FromResult(players);
        }
    }

    /// <summary>构造带缓存装饰器的统一入口，缓存默认开启。</summary>
    private PlayerStatsService CreateService(IPlayerStatsProvider provider, bool cacheEnabled = true)
    {
        var cached = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions { Enabled = cacheEnabled });
        return new PlayerStatsService(cached, new LogService());
    }

    [Fact]
    public async Task FetchAndCache_PersistsResolvedPlayers()
    {
        var provider = new FakeProvider();
        var service = CreateService(provider);
        var players = new List<PlayerRecord>
        {
            MakePlayer("Alpha", "-1"),
            MakePlayer("Beta", "-1"),
        };

        var outcome = await service.FetchAndCacheAsync(players);

        Assert.Equal(2, outcome.Processed);
        // 哨兵玩家解析出真账号后确实落库了，下次可直接命中缓存。
        foreach (var p in players)
        {
            var snapshot = await repository.GetLatestSnapshotAsync(p.AccountId, GameServer.Asia);
            Assert.NotNull(snapshot);
            Assert.Equal(p.AccountId, snapshot!.AccountId);
        }
    }

    [Fact]
    public async Task FetchAndCache_SendsWholeRosterInOneCall()
    {
        // 整局名单一次交给提供商：账号统计与军团标签本就按 100 个账号批量，
        // 切块只会把同一局拆成多轮重复请求。
        var provider = new FakeProvider();
        var service = CreateService(provider);
        var players = Enumerable.Range(0, 20)
            .Select(i => MakePlayer($"Player{i:00}", $"{3000 + i}"))
            .ToList();

        await service.FetchAndCacheAsync(players);

        Assert.Equal(1, provider.Calls);
        foreach (var p in players)
        {
            var snapshot = await repository.GetLatestSnapshotAsync(p.AccountId, GameServer.Asia);
            Assert.NotNull(snapshot);
        }
    }

    [Fact]
    public async Task FetchAndCache_ProviderFailure_ReportsZeroProcessed()
    {
        // 整批失败时返回零处理，收敛循环据此判定无进展，不会误以为拉到了数据。
        var provider = new FakeProvider { FailFromCall = 1 };
        var service = CreateService(provider);
        var players = Enumerable.Range(0, 20)
            .Select(i => MakePlayer($"Player{i:00}", $"{3100 + i}"))
            .ToList();

        var outcome = await service.FetchAndCacheAsync(players);

        Assert.Equal(0, outcome.Processed);
        Assert.True(outcome.AllFailed);
        var first = await repository.GetLatestSnapshotAsync(players[0].AccountId, GameServer.Asia);
        Assert.Null(first);
    }

    [Fact]
    public async Task FetchAndCache_SkipsBotsAndEmptyNames()
    {
        var provider = new FakeProvider();
        var service = CreateService(provider);
        var players = new List<PlayerRecord>
        {
            new() { AccountId = "-1", PlayerName = ":bot", IsBot = true },
            new() { AccountId = "-1", PlayerName = string.Empty },
            MakePlayer("Real", "-1"),
        };

        var outcome = await service.FetchAndCacheAsync(players);

        Assert.Equal(1, outcome.Processed);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task FetchAndCache_ResolvesAccountFromLocalHistory()
    {
        // 历史里已有该昵称的真账号，赛前名单的 -1 哨兵应被本地历史直接换掉，
        // 免去一次 account/list 请求，账号级快照也能立刻命中缓存。
        await repository.SaveBattleAsync(new BattleRecord
        {
            BattleId = "hist-1",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Server = GameServer.Asia,
            Players =
            [
                new PlayerRecord
                {
                    AccountId = "2999",
                    PlayerName = "KnownPlayer",
                    Server = GameServer.Asia,
                    IsAlly = true,
                },
            ],
        });

        var provider = new FakeProvider();
        var service = CreateService(provider);
        var player = new PlayerRecord
        {
            AccountId = "-1",
            PlayerName = "KnownPlayer",
            Server = GameServer.Asia,
        };

        await service.FetchAndCacheAsync([player]);

        Assert.Equal("2999", player.AccountId);
        var snapshot = await repository.GetLatestSnapshotAsync("2999", GameServer.Asia);
        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task CachingProvider_SecondFetchHitsCacheWithoutNetwork()
    {
        // 首轮联网拉取并落库，分舰种齐备。次轮同玩家、同一条船应直接命中缓存，
        // 不再产生网络调用。
        var provider = new FakeProvider { FillClassStats = true };
        var cached = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions { Enabled = true });

        var first = MakePlayer("Cached", "2001");
        first.ClassStatsJson = string.Empty;
        await cached.FetchStatsAsync([first]);

        var second = MakePlayer("Cached", "2001");
        second.ClassStatsJson = string.Empty;
        second.ShipClass = ShipClass.Cruiser;
        second.AccountBattles = 1000;
        second.AccountBattlesSolo = 0;
        second.AccountWinrateSolo = 0;
        second.AccountBattlesDiv2 = 0;
        second.AccountWinrateDiv2 = 0;
        second.AccountBattlesDiv3 = 0;
        second.AccountWinrateDiv3 = 0;
        second.WeightedWinrate = 0;
        second.AccountAvgDamage = 0;
        second.ShipTier = 10;
        int callsBeforeSecond = provider.Calls;
        await cached.FetchStatsAsync([second]);
        

        // 账号胜率、单船、分舰种三项均由缓存灌回，提供商不再被调用。
        Assert.Equal(callsBeforeSecond, provider.Calls);
        Assert.Equal(0.55, second.AccountWinrate, 3);
        Assert.Equal(100, second.ShipBattles, 3);
        Assert.False(string.IsNullOrEmpty(second.ClassStatsJson));
    }
    [Fact]
    public async Task CachingProvider_ExpiredTtl_FallsBackToNetwork()
    {
        // TTL 置零，任何缓存都视为过期，次轮必须重新联网。
        var provider = new FakeProvider { FillClassStats = true };
        var cached = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions
            {
                Enabled = true,
                Ttl = TimeSpan.Zero,
            });

        await cached.FetchStatsAsync([MakePlayer("Expired", "2002")]);
        await cached.FetchStatsAsync([MakePlayer("Expired", "2002")]);

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task CachingProvider_Disabled_SkipsCacheReadButStillPersists()
    {
        // 关缓存时每次都走网络，但快照仍应落库：快照是训练数据原料，
        // 不随加速开关关闭。
        var provider = new FakeProvider { FillClassStats = true };
        var cached = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions { Enabled = false });

        await cached.FetchStatsAsync([MakePlayer("NoCache", "2003")]);
        await cached.FetchStatsAsync([MakePlayer("NoCache", "2003")]);

        Assert.Equal(2, provider.Calls);
        var snapshot = await repository.GetLatestSnapshotAsync("2003", GameServer.Asia);
        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task CachingProvider_Disabled_DoesNotHydrateFromCache()
    {
        // 先落一条有效快照，再关闭加速，此时不应从缓存灌数据，必须联网。
        var provider = new FakeProvider { FillClassStats = true };
        var enabled = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions { Enabled = true });
        await enabled.FetchStatsAsync([MakePlayer("Seeded", "2004")]);
        Assert.Equal(1, provider.Calls);

        var disabled = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions { Enabled = false });
        var again = MakePlayer("Seeded", "2004");
        again.ClassStatsJson = string.Empty;
        await disabled.FetchStatsAsync([again]);

        // 关闭时跳过命中，因此又走了一次网络。
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task CachingProvider_HydratesClanTagButKeepsReplayValue()
    {
        var provider = new FakeProvider { FillClassStats = true, ClanTag = "[CACHED]" };
        var cached = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions { Enabled = true });

        // 首轮联网，把带军团标签的快照落库。
        await cached.FetchStatsAsync([MakePlayer("Clan1", "3001")]);

        // 次轮带回放来源的标签，缓存命中灌数据时不得覆盖它。
        var withReplayClan = MakePlayer("Clan1", "3001");
        withReplayClan.ClanTag = "[REPLAY]";
        withReplayClan.ClassStatsJson = string.Empty;
        await cached.FetchStatsAsync([withReplayClan]);

        Assert.Equal("[REPLAY]", withReplayClan.ClanTag);

        // 次轮无标签时从缓存补上。
        var noClan = MakePlayer("Clan1", "3001");
        noClan.ClassStatsJson = string.Empty;
        await cached.FetchStatsAsync([noClan]);

        Assert.Equal("[CACHED]", noClan.ClanTag);
    }

    [Fact]
    public async Task CachingProvider_PersistsClanTagForHiddenPlayer()
    {
        // 隐藏战绩玩家没有统计，但军团标签应落库，供后续跨局回填。
        var provider = new FakeProvider { ClanTag = "[HID]" };
        var cached = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions { Enabled = true });

        var hidden = new PlayerRecord
        {
            AccountId = "3002",
            PlayerName = "HiddenGuy",
            Server = GameServer.Asia,
            IsHidden = true,
            ClanTag = string.Empty,
        };

        await cached.FetchStatsAsync([hidden]);

        var snapshot = await repository.GetLatestSnapshotAsync("3002", GameServer.Asia);
        Assert.NotNull(snapshot);
        Assert.Equal("[HID]", snapshot!.ClanTag);
    }

    [Fact]
    public async Task CachingProvider_HiddenSnapshot_DoesNotHydrateFakeZeroWinrate()
    {
        // 隐藏玩家只落军团标签的空快照。次轮灌缓存时不能把空快照当统计，
        // 否则账号胜率会从 -1 被改成 0，界面上隐藏战绩就变成 0.0% 了。
        var provider = new FakeProvider { ClanTag = "[HID]", MarkHidden = true };
        var cached = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions { Enabled = true });

        await cached.FetchStatsAsync([new PlayerRecord
        {
            AccountId = "3003",
            PlayerName = "HiddenGuy",
            Server = GameServer.Asia,
            IsHidden = true,
        }]);

        var again = new PlayerRecord
        {
            AccountId = "3003",
            PlayerName = "HiddenGuy",
            Server = GameServer.Asia,
            IsHidden = true,
        };
        await cached.FetchStatsAsync([again]);

        Assert.Equal(-1, again.AccountWinrate, 3);
        Assert.Equal("[HID]", again.ClanTag);
    }

    [Fact]
    public async Task CachingProvider_ForceFetch_BypassesFullyCoveredShortCircuit()
    {
        // 缓存齐全的玩家普通查询会短路，强制刷新必须照样联网重新拉，
        // 否则手动刷新对已覆盖玩家就是空操作。
        var provider = new FakeProvider { FillClassStats = true };
        var cached = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions { Enabled = true });

        // 首轮联网落库，玩家已 IsFullyCovered。
        await cached.FetchStatsAsync([MakePlayer("Covered", "4001")]);
        Assert.Equal(1, provider.Calls);

        // 强制刷新：记录齐全也要发一次网络请求。
        var refreshed = MakePlayer("Covered", "4001");
        refreshed.ClassStatsJson = "{\"CA\":{\"battles\":100,\"wins\":55,\"damage\":50000}}";
        await cached.ForceFetchStatsAsync([refreshed]);

        Assert.Equal(2, provider.Calls);
        Assert.Equal(0.55, refreshed.AccountWinrate, 3);
    }

    [Fact]
    public async Task CachingProvider_ForceFetch_ClearsStaleStatsBeforeRefetch()
    {
        // 手动刷新前界面侧先把统计复位成哨兵。强制拉取应基于复位后的记录
        // 重新联网并写回新值，不能因为缺数据判定路径变化而丢数据。
        var provider = new FakeProvider { FillClassStats = true };
        var cached = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions { Enabled = true });

        await cached.FetchStatsAsync([MakePlayer("Stale", "4002")]);
        Assert.Equal(1, provider.Calls);

        var refreshed = MakePlayer("Stale", "4002");
        refreshed.ResetStats();
        await cached.ForceFetchStatsAsync([refreshed]);

        Assert.Equal(2, provider.Calls);
        // 拉取后统计从哨兵恢复成真实值。
        Assert.Equal(0.55, refreshed.AccountWinrate, 3);
        Assert.Equal(1000, refreshed.AccountBattles, 3);
        Assert.False(string.IsNullOrEmpty(refreshed.ClassStatsJson));

        // 新快照照常落库，后续普通查询继续命中缓存。
        var snapshot = await repository.GetLatestSnapshotAsync("4002", GameServer.Asia);
        Assert.NotNull(snapshot);
        Assert.Equal(0.55, snapshot!.AccountWinrate, 3);
    }

    [Fact]
    public async Task GetAccountIdsByNames_ReturnsLatestRealAccount()
    {
        await repository.SaveBattleAsync(new BattleRecord
        {
            BattleId = "b1",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            Server = GameServer.Asia,
            Players =
            [
                new PlayerRecord { AccountId = "1001", PlayerName = "Dup", Server = GameServer.Asia, IsAlly = true },
            ],
        });
        await repository.SaveBattleAsync(new BattleRecord
        {
            BattleId = "b2",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Server = GameServer.Asia,
            Players =
            [
                new PlayerRecord { AccountId = "1002", PlayerName = "Dup", Server = GameServer.Asia, IsAlly = true },
                new PlayerRecord { AccountId = "-1", PlayerName = "NoHistory", Server = GameServer.Asia, IsAlly = true },
            ],
        });

        var mapping = await repository.GetAccountIdsByNamesAsync(
            ["Dup", "NoHistory"], GameServer.Asia);

        // 同名取最近一次入库的账号，没有历史的昵称不在映射里。
        Assert.Equal("1002", mapping["Dup"]);
        Assert.False(mapping.ContainsKey("NoHistory"));
    }
}
















