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

                p.AccountBattles = 1000;
                p.AccountWinrate = 0.55;
                p.ShipBattles = 100;
                p.ShipWinrate = 0.6;
                if (FillClassStats)
                {
                    p.ClassStatsJson = "{\"CA\":{\"battles\":100,\"wins\":55,\"damage\":50000}}";
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
        return new PlayerStatsService(cached, repository, new LogService());
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

        Assert.Equal(2, outcome.Cached);
        // 哨兵玩家解析出真账号后确实落库了，下次可直接命中缓存。
        foreach (var p in players)
        {
            var snapshot = await repository.GetLatestSnapshotAsync(p.AccountId, GameServer.Asia);
            Assert.NotNull(snapshot);
            Assert.Equal(p.AccountId, snapshot!.AccountId);
        }
    }

    [Fact]
    public async Task FetchAndCache_ProviderFailure_KeepsEarlierChunks()
    {
        // 每批 8 人，第 2 批开始失败。第 1 批的 8 人应已落库。
        var provider = new FakeProvider { FailFromCall = 2 };
        var service = CreateService(provider);
        var players = Enumerable.Range(0, 20)
            .Select(i => MakePlayer($"Player{i:00}", "-1"))
            .ToList();

        var outcome = await service.FetchAndCacheAsync(players);

        Assert.Equal(8, outcome.Cached);
        // 第 1 批的玩家命中缓存，后续批次的玩家没有。
        var first = await repository.GetLatestSnapshotAsync(players[0].AccountId, GameServer.Asia);
        Assert.NotNull(first);
        var later = await repository.GetLatestSnapshotAsync(players[15].AccountId, GameServer.Asia);
        Assert.Null(later);
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

        Assert.Equal(1, outcome.Cached);
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
        // 首轮联网拉取并落库，分舰种齐备。次轮同玩家应直接命中缓存，
        // 不再产生网络调用。
        var provider = new FakeProvider { FillClassStats = true };
        var cached = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions { Enabled = true });

        var first = MakePlayer("Cached", "2001");
        first.ClassStatsJson = string.Empty;
        await cached.FetchStatsAsync([first]);
        Assert.Equal(1, provider.Calls);

        var second = MakePlayer("Cached", "2001");
        second.ClassStatsJson = string.Empty;
        await cached.FetchStatsAsync([second]);

        // 账号胜率、单船、分舰种三项均由缓存灌回，提供商不再被调用。
        Assert.Equal(1, provider.Calls);
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
    public async Task CachingProvider_Disabled_PassesThrough()
    {
        // 关缓存时纯透传，每次调用都走网络，且不落库。
        var provider = new FakeProvider { FillClassStats = true };
        var cached = new CachingPlayerStatsProvider(
            provider, repository, new PlayerStatsCacheOptions { Enabled = false });

        await cached.FetchStatsAsync([MakePlayer("NoCache", "2003")]);
        await cached.FetchStatsAsync([MakePlayer("NoCache", "2003")]);

        Assert.Equal(2, provider.Calls);
        var snapshot = await repository.GetLatestSnapshotAsync("2003", GameServer.Asia);
        Assert.Null(snapshot);
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
