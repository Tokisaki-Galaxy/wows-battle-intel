using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Storage.Tests;

public class SqliteBattleRepositoryTests : IAsyncLifetime
{
    private string dbPath = string.Empty;
    private SqliteBattleRepository repository = null!;

    public async Task InitializeAsync()
    {
        // 每个测试用独立临时文件，避免内存库在连接关闭后丢失。
        // 系统盘空间紧张，临时文件落到仓库内的 .test-tmp，用完即删。
        dbPath = Path.Combine(TestTempRoot(), $"wbi-test-{Guid.NewGuid():N}.db");
        repository = new SqliteBattleRepository(dbPath);
        await repository.InitializeAsync();
    }

    /// <summary>测试临时目录，位于仓库内。</summary>
    private static string TestTempRoot()
    {
        string? dir = Path.GetDirectoryName(typeof(SqliteBattleRepositoryTests).Assembly.Location);
        for (int i = 0; dir is not null && i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "WowsBattleIntel.slnx")))
            {
                string root = Path.Combine(dir, ".test-tmp");
                Directory.CreateDirectory(root);
                return root;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return Path.GetTempPath();
    }

    public Task DisposeAsync()
    {
        TryDelete(dbPath);
        TryDelete(dbPath + "-wal");
        TryDelete(dbPath + "-shm");
        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响测试结论。
        }
    }

    private static BattleRecord MakeBattle(string battleId = "battle-001", BattleOutcome outcome = BattleOutcome.Win)
    {
        return new BattleRecord
        {
            BattleId = battleId,
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(1753163342000),
            FinishedAt = DateTimeOffset.FromUnixTimeMilliseconds(1753164260000),
            MapId = "spaces/50_Gold_harbor",
            BattleType = BattleType.Random,
            GameVersion = "15.6.0",
            Server = GameServer.Asia,
            Outcome = outcome,
            DurationSeconds = 918,
            Players =
            [
                new PlayerRecord
                {
                    AccountId = "1001",
                    PlayerName = "AllyOne",
                    Server = GameServer.Asia,
                    ClanTag = "[TEST]",
                    IsAlly = true,
                    ShipId = "PASS710",
                    ShipName = "Archerfish",
                    ShipTier = 10,
                    ShipClass = ShipClass.Submarine,
                    DivisionSize = 3,
                    DamageDealt = 81230,
                    Kills = 1,
                    Survived = true,
                    SurvivedSeconds = 918,
                    ExpEarned = 2100,
                },
                new PlayerRecord
                {
                    AccountId = "2001",
                    PlayerName = "EnemyOne",
                    Server = GameServer.Asia,
                    IsAlly = false,
                    ShipId = "PRSS508",
                    ShipName = "S-189",
                    ShipTier = 8,
                    ShipClass = ShipClass.Submarine,
                    DamageDealt = 1169,
                    Survived = false,
                    SurvivedSeconds = 620,
                },
            ],
        };
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsBattle()
    {
        var battle = MakeBattle();

        await repository.SaveBattleAsync(battle);
        var loaded = await repository.GetBattleAsync(battle.BattleId);

        Assert.NotNull(loaded);
        Assert.Equal(battle.BattleId, loaded.BattleId);
        Assert.Equal("spaces/50_Gold_harbor", loaded.MapId);
        Assert.Equal(BattleType.Random, loaded.BattleType);
        Assert.Equal(GameServer.Asia, loaded.Server);
        Assert.Equal(BattleOutcome.Win, loaded.Outcome);
        Assert.Equal(918, loaded.DurationSeconds);
        Assert.Equal(2, loaded.Players.Count);
    }

    [Fact]
    public async Task SaveAndGet_PreservesPlayerBattleStats()
    {
        var battle = MakeBattle();

        await repository.SaveBattleAsync(battle);
        var loaded = await repository.GetBattleAsync(battle.BattleId);

        var ally = loaded!.Players.First(p => p.AccountId == "1001");
        Assert.Equal(81230, ally.DamageDealt);
        Assert.Equal(1, ally.Kills);
        Assert.True(ally.Survived);
        Assert.Equal(918, ally.SurvivedSeconds);
        Assert.Equal(2100, ally.ExpEarned);
        Assert.Equal(3, ally.DivisionSize);
        Assert.Equal(ShipClass.Submarine, ally.ShipClass);
        Assert.Equal("[TEST]", ally.ClanTag);
    }

    [Fact]
    public async Task SaveBattle_IsIdempotent()
    {
        var battle = MakeBattle();

        await repository.SaveBattleAsync(battle);
        await repository.SaveBattleAsync(battle);
        await repository.SaveBattleAsync(battle);

        var loaded = await repository.GetBattleAsync(battle.BattleId);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Players.Count);
    }

    [Fact]
    public async Task SaveBattle_UpdatesExistingOnResave()
    {
        var battle = MakeBattle();
        await repository.SaveBattleAsync(battle);

        // 同一局，结果更新。
        battle.Outcome = BattleOutcome.Loss;
        battle.DurationSeconds = 1000;
        await repository.SaveBattleAsync(battle);

        var loaded = await repository.GetBattleAsync(battle.BattleId);
        Assert.Equal(BattleOutcome.Loss, loaded!.Outcome);
        Assert.Equal(1000, loaded.DurationSeconds);
    }

    [Fact]
    public async Task GetBattle_UnknownId_ReturnsNull()
    {
        var loaded = await repository.GetBattleAsync("does-not-exist");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task SavePrediction_AndBackfillOutcome_EnablesCalibration()
    {
        var battle = MakeBattle();
        await repository.SaveBattleAsync(battle);

        var prediction = new PredictionResult
        {
            BattleId = battle.BattleId,
            PredictedAt = DateTimeOffset.UtcNow,
            AllyWinProbability = 0.62,
            Confidence = 1.0,
            ModelVersion = "baseline-logistic-v1",
            FeatureSnapshotJson = """{"test":true}""",
        };
        await repository.SavePredictionAsync(prediction);

        // 未回填前，不应出现在待校准列表。
        var before = await repository.GetUncalibratedPredictionsAsync(100);
        Assert.Empty(before);

        // 回填真实结果。
        await repository.BackfillOutcomeAsync(battle.BattleId, BattleOutcome.Win);

        var after = await repository.GetUncalibratedPredictionsAsync(100);
        var loaded = Assert.Single(after);
        Assert.Equal(battle.BattleId, loaded.BattleId);
        Assert.Equal(0.62, loaded.AllyWinProbability, precision: 6);
        Assert.Equal("baseline-logistic-v1", loaded.ModelVersion);
    }

    [Fact]
    public async Task BackfillOutcome_DoesNotOverwriteExistingResult()
    {
        var battle = MakeBattle();
        await repository.SaveBattleAsync(battle);

        var prediction = new PredictionResult
        {
            BattleId = battle.BattleId,
            PredictedAt = DateTimeOffset.UtcNow,
            AllyWinProbability = 0.5,
        };
        await repository.SavePredictionAsync(prediction);

        await repository.BackfillOutcomeAsync(battle.BattleId, BattleOutcome.Win);
        // 再次回填不应改变已有结果。
        await repository.BackfillOutcomeAsync(battle.BattleId, BattleOutcome.Loss);

        var results = await repository.GetUncalibratedPredictionsAsync(100);
        Assert.Single(results);
    }

    [Fact]
    public async Task SaveBattle_MultipleBattles_AllRetrievable()
    {
        for (int i = 0; i < 5; i++)
        {
            await repository.SaveBattleAsync(MakeBattle($"battle-{i:D3}"));
        }

        for (int i = 0; i < 5; i++)
        {
            var loaded = await repository.GetBattleAsync($"battle-{i:D3}");
            Assert.NotNull(loaded);
        }
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        // 重复初始化不应报错。
        await repository.InitializeAsync();
        await repository.InitializeAsync();

        await repository.SaveBattleAsync(MakeBattle());
        Assert.NotNull(await repository.GetBattleAsync("battle-001"));
    }

    [Fact]
    public async Task GetCalibrationSamples_ReturnsPredictionsWithOutcome()
    {
        var battle = MakeBattle();
        await repository.SaveBattleAsync(battle);

        await repository.SavePredictionAsync(new PredictionResult
        {
            BattleId = battle.BattleId,
            PredictedAt = DateTimeOffset.UtcNow,
            AllyWinProbability = 0.7,
        });
        await repository.BackfillOutcomeAsync(battle.BattleId, BattleOutcome.Win);

        var samples = await repository.GetCalibrationSamplesAsync(100);

        var sample = Assert.Single(samples);
        Assert.Equal(battle.BattleId, sample.Prediction.BattleId);
        Assert.Equal(BattleOutcome.Win, sample.Outcome);
        Assert.Equal(0.7, sample.Prediction.AllyWinProbability, precision: 6);
    }

    [Fact]
    public async Task GetCalibrationSamples_ExcludesUnresolvedOutcomes()
    {
        var battle = MakeBattle();
        await repository.SaveBattleAsync(battle);

        // 未回填结果，不应进入校准样本。
        await repository.SavePredictionAsync(new PredictionResult
        {
            BattleId = battle.BattleId,
            PredictedAt = DateTimeOffset.UtcNow,
            AllyWinProbability = 0.5,
        });

        var samples = await repository.GetCalibrationSamplesAsync(100);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task GetCalibrationSamples_IncludesAlreadyCalibratedRows()
    {
        var battle = MakeBattle();
        await repository.SaveBattleAsync(battle);
        await repository.SavePredictionAsync(new PredictionResult
        {
            BattleId = battle.BattleId,
            PredictedAt = DateTimeOffset.UtcNow,
            AllyWinProbability = 0.4,
        });
        await repository.BackfillOutcomeAsync(battle.BattleId, BattleOutcome.Loss);

        // 校准是可重复的计算，标记为已校准后仍应返回。
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE predictions SET calibrated = 1;";
            await command.ExecuteNonQueryAsync();
        }

        var samples = await repository.GetCalibrationSamplesAsync(100);
        Assert.Single(samples);
    }

    [Fact]
    public async Task GetRecentBattles_OrdersByStartTimeDescending()
    {
        for (int i = 0; i < 3; i++)
        {
            var battle = MakeBattle($"battle-{i:D3}");
            battle.StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(1753163342000 + i * 1000);
            await repository.SaveBattleAsync(battle);
        }

        var recent = await repository.GetRecentBattlesAsync(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal("battle-002", recent[0].BattleId);
        Assert.Equal("battle-001", recent[1].BattleId);
    }

    [Fact]
    public async Task GetRecentBattles_ZeroLimit_ReturnsEmpty()
    {
        await repository.SaveBattleAsync(MakeBattle());
        Assert.Empty(await repository.GetRecentBattlesAsync(0));
    }

    [Fact]
    public async Task CountBattles_CountsStoredBattles()
    {
        Assert.Equal(0, await repository.CountBattlesAsync());

        await repository.SaveBattleAsync(MakeBattle("battle-a"));
        await repository.SaveBattleAsync(MakeBattle("battle-b"));

        Assert.Equal(2, await repository.CountBattlesAsync());
    }

    [Fact]
    public async Task FindEquivalentBattle_MatchesSameBattleFromAnotherReplay()
    {
        var first = MakeBattle("battle-a");
        await repository.SaveBattleAsync(first);

        // 同一局另一名玩家的回放：battle_id 不同，参战者与地图相同，开始时间差 5 分钟。
        var second = MakeBattle("battle-b");
        second.StartedAt = first.StartedAt.AddMinutes(5);

        var found = await repository.FindEquivalentBattleAsync(second);

        Assert.NotNull(found);
        Assert.Equal("battle-a", found.BattleId);
    }

    [Fact]
    public async Task FindEquivalentBattle_DifferentRoster_ReturnsNull()
    {
        var first = MakeBattle("battle-a");
        await repository.SaveBattleAsync(first);

        var second = MakeBattle("battle-b");
        second.Players = second.Players.Select(p => new PlayerRecord
        {
            AccountId = p.AccountId == "1001" ? "9999" : p.AccountId,
            Server = p.Server,
            IsAlly = p.IsAlly,
        }).ToList();

        Assert.Null(await repository.FindEquivalentBattleAsync(second));
    }

    [Fact]
    public async Task FindEquivalentBattle_BeyondTimeWindow_ReturnsNull()
    {
        var first = MakeBattle("battle-a");
        await repository.SaveBattleAsync(first);

        // 差 800 秒，超出 760 秒窗口。
        var second = MakeBattle("battle-b");
        second.StartedAt = first.StartedAt.AddSeconds(800);

        Assert.Null(await repository.FindEquivalentBattleAsync(second));
    }

    [Fact]
    public async Task FindEquivalentBattle_DifferentMap_ReturnsNull()
    {
        var first = MakeBattle("battle-a");
        await repository.SaveBattleAsync(first);

        var second = MakeBattle("battle-b");
        second.MapId = "spaces/17_NI_farmer";

        Assert.Null(await repository.FindEquivalentBattleAsync(second));
    }

    [Fact]
    public async Task SaveSnapshot_RoundTripsAccountAvgDamage()
    {
        var snapshot = new WowsBattleIntel.Core.Abstractions.PlayerSnapshot
        {
            AccountId = "1001",
            Server = GameServer.Asia,
            CapturedAt = DateTimeOffset.UtcNow,
            AccountBattles = 1000,
            AccountWinrate = 0.52,
            AccountAvgDamage = 61700,
            WeightedWinrate = 0.52,
        };

        await repository.SaveSnapshotAsync(snapshot);

        var loaded = await repository.GetLatestSnapshotBeforeAsync(
            "1001", GameServer.Asia, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.NotNull(loaded);
        Assert.Equal(61700, loaded.AccountAvgDamage, precision: 6);
    }

    [Fact]
    public async Task SaveSnapshot_RoundTripsShipStats()
    {
        var snapshot = new WowsBattleIntel.Core.Abstractions.PlayerSnapshot
        {
            AccountId = "1002",
            Server = GameServer.Asia,
            CapturedAt = DateTimeOffset.UtcNow,
            AccountBattles = 800,
            AccountWinrate = 0.5,
            ShipId = "SHIP-A",
            ShipBattles = 230,
            ShipWinrate = 0.62,
            ShipAvgDamage = 71200,
        };

        await repository.SaveSnapshotAsync(snapshot);

        var loaded = await repository.GetLatestSnapshotBeforeAsync(
            "1002", GameServer.Asia, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.NotNull(loaded);
        Assert.Equal("SHIP-A", loaded.ShipId);
        Assert.Equal(230, loaded.ShipBattles);
        Assert.Equal(0.62, loaded.ShipWinrate, precision: 6);
        Assert.Equal(71200, loaded.ShipAvgDamage, precision: 6);
    }

    [Fact]
    public async Task SaveSnapshot_AccountLevel_KeepsShipSentinels()
    {
        // 账号级快照不带 ship_id，读回时单船字段保持 -1 哨兵。
        var snapshot = new WowsBattleIntel.Core.Abstractions.PlayerSnapshot
        {
            AccountId = "1003",
            Server = GameServer.Asia,
            CapturedAt = DateTimeOffset.UtcNow,
            AccountBattles = 500,
            AccountWinrate = 0.5,
        };

        await repository.SaveSnapshotAsync(snapshot);

        var loaded = await repository.GetLatestSnapshotBeforeAsync(
            "1003", GameServer.Asia, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.NotNull(loaded);
        Assert.Equal(string.Empty, loaded.ShipId);
        Assert.Equal(-1, loaded.ShipBattles);
        Assert.Equal(-1, loaded.ShipWinrate);
        Assert.Equal(-1, loaded.ShipAvgDamage);
    }

    [Fact]
    public async Task UpdatePlayerShipInfo_RebuildsShipId_KeepsPerformanceColumns()
    {
        var battle = MakeBattle();
        await repository.SaveBattleAsync(battle);

        // 模拟存量脏数据：ship_id 是内部索引码，船信息过时，表现列保持库中原值。
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE battle_players SET
                    ship_id = 'PASS710', ship_name = 'OldName', ship_tier = 5,
                    ship_type = 'Unknown', clan_tag = '', division_size = 1,
                    damage_dealt = 999, kills = 7, exp_earned = 555
                WHERE battle_id = 'battle-001' AND account_id = '1001';
                """;
            await command.ExecuteNonQueryAsync();
        }

        var rebuilt = new List<PlayerRecord>
        {
            new()
            {
                AccountId = "1001",
                Server = GameServer.Asia,
                PlayerName = "AllyOne",
                ClanTag = "[TEST]",
                ShipId = "3445536752",
                ShipName = "Archerfish",
                ShipTier = 10,
                ShipClass = ShipClass.Submarine,
                DivisionSize = 3,
                DamageDealt = 1,
                Kills = 0,
            },
        };

        int updated = await repository.UpdatePlayerShipInfoAsync(battle.BattleId, rebuilt);
        Assert.Equal(1, updated);

        var loaded = await repository.GetBattleAsync(battle.BattleId);
        Assert.NotNull(loaded);
        var ally = loaded.Players.Single(p => p.AccountId == "1001");

        // 船信息与身份列应重建为映射结果。
        Assert.Equal("3445536752", ally.ShipId);
        Assert.Equal("Archerfish", ally.ShipName);
        Assert.Equal(10, ally.ShipTier);
        Assert.Equal(ShipClass.Submarine, ally.ShipClass);
        Assert.Equal("[TEST]", ally.ClanTag);
        Assert.Equal(3, ally.DivisionSize);

        // 对局表现列不被覆盖。
        Assert.Equal(999, ally.DamageDealt);
        Assert.Equal(7, ally.Kills);
        Assert.Equal(555, ally.ExpEarned);
    }

    [Fact]
    public async Task UpdatePlayerShipInfo_UnknownIdentity_UpdatesNothing()
    {
        var battle = MakeBattle();
        await repository.SaveBattleAsync(battle);

        var mismatch = new List<PlayerRecord>
        {
            new()
            {
                AccountId = "no-such-account",
                Server = GameServer.Asia,
                ShipId = "3445536752",
            },
        };

        int updated = await repository.UpdatePlayerShipInfoAsync(battle.BattleId, mismatch);
        Assert.Equal(0, updated);
    }
}
