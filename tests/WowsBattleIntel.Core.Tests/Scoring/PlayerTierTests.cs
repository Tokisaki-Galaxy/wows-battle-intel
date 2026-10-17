using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Core.Tests.Scoring;

/// <summary>
/// 玩家档位判定的边界。
/// </summary>
/// <remarks>
/// 界面文案与胜率列着色都走这一处判定。阈值一旦调整，这里先失败，
/// 避免名单上出现「文案说普通、颜色说海王」的分叉。
/// </remarks>
public class PlayerTierTests
{
    [Theory]
    [InlineData(0.58, 500, PlayerTier.Normal)] // 胜率刚好卡在阈值线上，不算海王
    [InlineData(0.5801, 500, PlayerTier.Unicum)]
    [InlineData(0.70, 499, PlayerTier.Normal)] // 胜率高但场次不足，避免小样本误判
    [InlineData(0.70, 500, PlayerTier.Unicum)]
    [InlineData(0.47, 500, PlayerTier.Ape)] // 猴子阈值取小于等于
    [InlineData(0.4701, 500, PlayerTier.Normal)]
    [InlineData(0.30, 10, PlayerTier.Ape)]
    public void ClassifyPlayer_UsesSharedThresholds(
        double winrate,
        double battles,
        PlayerTier expected)
    {
        Assert.Equal(expected, TeamAggregator.ClassifyPlayer(winrate, battles));
    }

    [Fact]
    public void DefaultThresholds_MatchDocumentedValues()
    {
        // 阈值沿用 ApeRadar 默认口径，与 Python 端 features.py 保持一致。
        Assert.Equal(0.47, TeamAggregator.DefaultApeWinrateThreshold);
        Assert.Equal(0.58, TeamAggregator.DefaultUnicumWinrateThreshold);
        Assert.Equal(500, TeamAggregator.DefaultUnicumBattlesThreshold);
    }
}
