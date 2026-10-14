using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Prediction.Tests;

public class FeatureVectorBuilderTests
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

    [Fact]
    public void FeatureNames_MatchesPythonContract()
    {
        // 这个顺序是与 Python 端 MatchFeatures.feature_names() 的契约。
        // 任何变动都会导致推理输入错位，必须同步更新两端并重新导出模型。
        string[] expected =
        [
            "winrate_diff",
            "weighted_winrate_diff",
            "recent_winrate_diff",
            "battles_diff",
            "unicum_diff",
            "ape_diff",
            "tier_diff",
            "player_count_diff",
            "ally_winrate",
            "enemy_winrate",
            "ally_weighted",
            "enemy_weighted",
            "ally_battles",
            "enemy_battles",
            "ally_top_winrate",
            "enemy_top_winrate",
            "ally_ape_count",
            "enemy_ape_count",
            "ally_unicum_count",
            "enemy_unicum_count",
        ];

        Assert.Equal(expected, FeatureVectorBuilder.FeatureNames);
    }

    [Fact]
    public void FeatureCount_IsTwenty()
    {
        Assert.Equal(20, FeatureVectorBuilder.FeatureCount);
    }

    [Fact]
    public void FromBattle_ProducesVectorOfCorrectLength()
    {
        var battle = new BattleRecord
        {
            Players =
            [
                MakePlayer("1", true, 0.60, 1000),
                MakePlayer("2", false, 0.45, 1000),
            ],
        };

        var vector = FeatureVectorBuilder.FromBattle(battle);

        Assert.Equal(FeatureVectorBuilder.FeatureCount, vector.Values.Length);
    }

    [Fact]
    public void FromBattle_ComputesWinrateDiffCorrectly()
    {
        var battle = new BattleRecord
        {
            Players =
            [
                MakePlayer("1", true, 0.60, 1000),
                MakePlayer("2", false, 0.45, 1000),
            ],
        };

        var vector = FeatureVectorBuilder.FromBattle(battle);

        // 第一个特征是胜率差。
        Assert.Equal(0.15f, vector.Values[0], precision: 5);
        // 第九个特征是友方胜率。
        Assert.Equal(0.60f, vector.Values[8], precision: 5);
        // 第十个特征是敌方胜率。
        Assert.Equal(0.45f, vector.Values[9], precision: 5);
    }

    [Fact]
    public void FromBattle_EvenTeams_ProducesZeroDiffs()
    {
        var battle = new BattleRecord
        {
            Players =
            [
                MakePlayer("1", true, 0.50, 1000),
                MakePlayer("2", false, 0.50, 1000),
            ],
        };

        var vector = FeatureVectorBuilder.FromBattle(battle);

        Assert.Equal(0f, vector.Values[0], precision: 5);
        Assert.Equal(0f, vector.Values[1], precision: 5);
    }

    [Fact]
    public void ToTensor_ProducesSingleRowMatrix()
    {
        var vector = new FeatureVector { Values = [1f, 2f, 3f] };

        var tensor = vector.ToTensor();

        Assert.Equal(1, tensor.GetLength(0));
        Assert.Equal(3, tensor.GetLength(1));
        Assert.Equal(1f, tensor[0, 0]);
        Assert.Equal(3f, tensor[0, 2]);
    }

    [Fact]
    public void FromBattle_EmptyBattle_HandlesGracefully()
    {
        var battle = new BattleRecord { Players = [] };

        var vector = FeatureVectorBuilder.FromBattle(battle);

        Assert.Equal(FeatureVectorBuilder.FeatureCount, vector.Values.Length);
        Assert.All(vector.Values, v => Assert.Equal(0f, v, precision: 5));
    }
}

public class PredictorFactoryTests
{
    [Fact]
    public void Create_NullPath_FallsBackToBaseline()
    {
        var result = PredictorFactory.Create(null, "test");

        Assert.True(result.UsedFallback);
        Assert.NotNull(result.FallbackReason);
        Assert.IsType<WowsBattleIntel.Core.Prediction.BaselinePredictor>(result.Predictor);
    }

    [Fact]
    public void Create_MissingFile_FallsBackToBaseline()
    {
        var result = PredictorFactory.Create(
            @"C:\nonexistent\model.onnx", "test");

        Assert.True(result.UsedFallback);
        Assert.Contains("不存在", result.FallbackReason);
    }

    [Fact]
    public void Create_InvalidModelFile_FallsBackToBaseline()
    {
        // 写一个内容非法的文件，加载应失败并降级。
        string tempPath = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.onnx");
        File.WriteAllText(tempPath, "not a real onnx model");

        try
        {
            var result = PredictorFactory.Create(tempPath, "test");

            Assert.True(result.UsedFallback);
            Assert.Contains("加载失败", result.FallbackReason);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
