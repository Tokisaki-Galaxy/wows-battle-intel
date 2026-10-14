namespace WowsBattleIntel.Client.ViewModels;

public class DisplayCalibrationTests
{
    [Theory]
    [InlineData(0.99, 0.95)]
    [InlineData(1.0, 0.95)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.0, 0.05)]
    [InlineData(0.003, 0.05)]
    [InlineData(0.96, 0.95)]
    public void ClampForDisplay_BoundsExtremeProbabilities(double input, double expected)
    {
        Assert.Equal(expected, DisplayCalibration.ClampForDisplay(input), precision: 10);
    }

    [Theory]
    [InlineData(0.85, "明显优势")]
    [InlineData(0.7, "明显优势")]
    [InlineData(0.6, "略占优")]
    [InlineData(0.55, "略占优")]
    [InlineData(0.5, "势均力敌")]
    [InlineData(0.46, "势均力敌")]
    [InlineData(0.45, "处于劣势")]
    [InlineData(0.35, "处于劣势")]
    [InlineData(0.3, "明显劣势")]
    [InlineData(0.1, "明显劣势")]
    public void TierText_BucketsByAllyPerspective(double probability, string expected)
    {
        Assert.Equal(expected, DisplayCalibration.TierText(probability));
    }

    [Theory]
    [InlineData(0.85, "WinBrush")]
    [InlineData(0.6, "AllyBrush")]
    [InlineData(0.5, "TextSecondaryBrush")]
    [InlineData(0.35, "HiddenBrush")]
    [InlineData(0.1, "LossBrush")]
    public void TierBrushKey_MatchesTierSeverity(double probability, string expected)
    {
        Assert.Equal(expected, DisplayCalibration.TierBrushKey(probability));
    }
}
