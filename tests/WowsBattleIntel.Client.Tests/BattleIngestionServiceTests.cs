using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Tests;

public class BattleIngestionServiceTests
{
    /// <summary>
    /// 固定输出预测器。记录是否被调用，供断言预测跳过逻辑。
    /// </summary>
    private sealed class StubPredictor : IPredictor
    {
        public int CallCount { get; private set; }

        public string ModelVersion => "stub";

        public string ModelHash => string.Empty;

        public Task<PredictionResult> PredictAsync(
            BattleRecord battle,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new PredictionResult
            {
                BattleId = battle.BattleId,
                PredictedAt = DateTimeOffset.UtcNow,
                AllyWinProbability = 0.5,
                ModelVersion = ModelVersion,
            });
        }
    }

    private static BattleRecord MakeBattle(
        string battleId,
        DateTimeOffset startedAt,
        BattleType type = BattleType.Random)
    {
        return new BattleRecord
        {
            BattleId = battleId,
            StartedAt = startedAt,
            MapId = "spaces/50_Gold_harbor",
            BattleType = type,
            Outcome = BattleOutcome.Win,
            Players =
            [
                new PlayerRecord { AccountId = "1001", IsAlly = true },
                new PlayerRecord { AccountId = "2002", IsAlly = false },
            ],
        };
    }

    [Fact]
    public async Task IngestAsync_SameBattleFromAnotherReplay_SkipsSaveAndPrediction()
    {
        var repository = new Storage.SqliteBattleRepository(TestServices.NewDatabasePath());
        await repository.InitializeAsync();
        var predictor = new StubPredictor();
        var service = new BattleIngestionService(CreateUnusedReplayService(), repository, predictor);

        var first = MakeBattle("battle-a", DateTimeOffset.FromUnixTimeMilliseconds(1753163342000));
        var second = MakeBattle("battle-b", first.StartedAt.AddMinutes(5));

        var result = await service.IngestParsedAsync(first);
        var secondResult = await service.IngestParsedAsync(second);

        Assert.True(result.Success);
        Assert.False(result.IsDuplicate);
        Assert.True(secondResult.Success);
        Assert.True(secondResult.IsDuplicate);
        Assert.Equal(1, await repository.CountBattlesAsync());
        Assert.Equal(1, predictor.CallCount);
    }

    [Fact]
    public async Task IngestAsync_NonPvpType_SavesBattleButSkipsPrediction()
    {
        var repository = new Storage.SqliteBattleRepository(TestServices.NewDatabasePath());
        await repository.InitializeAsync();
        var predictor = new StubPredictor();
        var service = new BattleIngestionService(CreateUnusedReplayService(), repository, predictor);

        var coop = MakeBattle(
            "battle-coop",
            DateTimeOffset.FromUnixTimeMilliseconds(1753163342000),
            BattleType.Coop);

        var result = await service.IngestParsedAsync(coop);

        Assert.True(result.Success);
        Assert.Contains("非 PvP", result.Message);
        Assert.Equal(1, await repository.CountBattlesAsync());
        Assert.Null(await repository.GetPredictionAsync("battle-coop"));
        Assert.Equal(0, predictor.CallCount);
    }

    /// <summary>
    /// 解析替身。测试走 IngestParsedAsync 绕过解析，实例本身不会被调用。
    /// </summary>
    private static ReplayService CreateUnusedReplayService() =>
        new(new Configuration.AppConfig());
}
