using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Storage.Tests;

public class SqliteBattleRepositoryTests : IAsyncLifetime
{
    private string dbPath = string.Empty;
    private SqliteBattleRepository repository = null!;

    public async Task InitializeAsync()
    {
        // 每个测试用独立临时文件，避免内存库在连接关闭后丢失。
        dbPath = Path.Combine(Path.GetTempPath(), $"wbi-test-{Guid.NewGuid():N}.db");
        repository = new SqliteBattleRepository(dbPath);
        await repository.InitializeAsync();
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
}
