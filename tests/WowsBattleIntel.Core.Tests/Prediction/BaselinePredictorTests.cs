using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Prediction;

namespace WowsBattleIntel.Core.Tests.Prediction;

public class BaselinePredictorTests
{
    private static PlayerRecord MakePlayer(string id, bool isAlly, double winrate, double battles)
    {
        return new PlayerRecord
        {
            AccountId = id,
            IsAlly = isAlly,
            AccountWinrate = winrate,
            AccountBattles = battles,
            AccountWinrateSolo = winrate,
            AccountBattlesSolo = battles,
            WeightedWinrate = winrate,
        };
    }

    private static BattleRecord MakeBattle(double allyWinrate, double enemyWinrate, int count = 6)
    {
        var players = new List<PlayerRecord>();
        for (int i = 0; i < count; i++)
        {
            players.Add(MakePlayer($"ally{i}", true, allyWinrate, 1000));
            players.Add(MakePlayer($"enemy{i}", false, enemyWinrate, 1000));
        }

        return new BattleRecord
        {
            BattleId = "test-battle",
            Players = players,
        };
    }

    [Fact]
    public async Task Predict_EvenTeams_ReturnsNearFiftyPercent()
    {
        var predictor = new BaselinePredictor();
        var battle = MakeBattle(0.50, 0.50);

        var result = await predictor.PredictAsync(battle);

        Assert.Equal(0.50, result.AllyWinProbability, precision: 6);
    }

    [Fact]
    public async Task Predict_StrongerAlly_ReturnsAboveFiftyPercent()
    {
        var predictor = new BaselinePredictor();
        var battle = MakeBattle(0.60, 0.45);

        var result = await predictor.PredictAsync(battle);

        Assert.True(result.AllyWinProbability > 0.5);
        Assert.True(result.AllyWinProbability < 1.0);
    }

    [Fact]
    public async Task Predict_WeakerAlly_ReturnsBelowFiftyPercent()
    {
        var predictor = new BaselinePredictor();
        var battle = MakeBattle(0.45, 0.60);

        var result = await predictor.PredictAsync(battle);

        Assert.True(result.AllyWinProbability < 0.5);
        Assert.True(result.AllyWinProbability > 0.0);
    }

    [Fact]
    public async Task Predict_ProbabilitiesAreSymmetric()
    {
        // 双方对调，胜率应互补。
        var predictor = new BaselinePredictor();

        var strongAlly = await predictor.PredictAsync(MakeBattle(0.60, 0.45));
        var weakAlly = await predictor.PredictAsync(MakeBattle(0.45, 0.60));

        Assert.Equal(1.0, strongAlly.AllyWinProbability + weakAlly.AllyWinProbability, precision: 6);
    }

    [Fact]
    public async Task Predict_FullCoverage_ReportsFeaturesAvailable()
    {
        var predictor = new BaselinePredictor();
        var battle = MakeBattle(0.55, 0.50);

        var result = await predictor.PredictAsync(battle);

        Assert.True(result.FeaturesAvailable);
        Assert.Equal(1.0, result.Confidence, precision: 6);
    }

    [Fact]
    public async Task Predict_HiddenPlayers_LowersConfidence()
    {
        var predictor = new BaselinePredictor();
        var players = new List<PlayerRecord>
        {
            MakePlayer("ally1", true, 0.55, 1000),
            new() { AccountId = "ally2", IsAlly = true, IsHidden = true, AccountWinrate = -1 },
            MakePlayer("enemy1", false, 0.50, 1000),
        };
        var battle = new BattleRecord { BattleId = "test", Players = players };

        var result = await predictor.PredictAsync(battle);

        Assert.False(result.FeaturesAvailable);
        Assert.True(result.Confidence < 1.0);
    }

    [Fact]
    public async Task Predict_ProducesFeatureSnapshot()
    {
        var predictor = new BaselinePredictor();
        var battle = MakeBattle(0.55, 0.50);

        var result = await predictor.PredictAsync(battle);

        Assert.False(string.IsNullOrWhiteSpace(result.FeatureSnapshotJson));
        var snapshot = FeatureSnapshot.Deserialize(result.FeatureSnapshotJson);
        Assert.NotNull(snapshot);
        Assert.Equal(6, snapshot.Ally.PlayerCount);
        Assert.Equal(0.55, snapshot.Ally.AvgAccountWinrate, precision: 6);
    }
}
