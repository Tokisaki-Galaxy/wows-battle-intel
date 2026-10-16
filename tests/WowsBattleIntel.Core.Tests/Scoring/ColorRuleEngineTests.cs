using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Core.Tests.Scoring;

/// <summary>
/// 染色规则求值测试。锁住"按顺序取第一条命中、未知值跳过、算子口径"三条。
/// </summary>
public class ColorRuleEngineTests
{
    private static readonly IReadOnlyList<ColorRule> DefaultRules = DefaultColorRules.Value;

    private static string Evaluate(
        IReadOnlyList<ColorRule> rules,
        double winrate = -1,
        double weighted = -1,
        double battles = -1,
        double avgDamage = -1,
        double shipWinrate = -1,
        double shipAvgDamage = -1) =>
        ColorRuleEngine.Evaluate(rules, winrate, weighted, battles, avgDamage, shipWinrate, shipAvgDamage);

    [Fact]
    public void DefaultRules_UnicumWinrate_Purple()
    {
        Assert.Equal("RowUnicumBrush", Evaluate(DefaultRules, winrate: 0.60));
    }

    [Fact]
    public void DefaultRules_ApeWinrate_Red()
    {
        Assert.Equal("RowApeBrush", Evaluate(DefaultRules, winrate: 0.45));
    }

    [Fact]
    public void DefaultRules_MiddleWinrate_NoColor()
    {
        Assert.Equal(string.Empty, Evaluate(DefaultRules, winrate: 0.52));
    }

    [Fact]
    public void DefaultRules_BoundaryValues()
    {
        // 默认紫规则用严格大于，0.58 不判紫；红规则用小于等于，0.47 判红。
        Assert.Equal(string.Empty, Evaluate(DefaultRules, winrate: 0.58));
        Assert.Equal("RowApeBrush", Evaluate(DefaultRules, winrate: 0.47));
    }

    [Fact]
    public void UnknownMetricValue_Skipped()
    {
        // 未知值用 -1 哨兵，规则不命中，不会因默认 0 被误判。
        Assert.Equal(string.Empty, Evaluate(DefaultRules));
    }

    [Fact]
    public void FirstMatchingRuleWins()
    {
        // 两条规则都命中时按顺序取第一条。
        var rules = new List<ColorRule>
        {
            new() { Metric = ColorRuleMetric.AccountWinrate, Operator = ColorRuleOperator.GreaterThan, Threshold = 0.5, RowBrushKey = "RowUnicumBrush" },
            new() { Metric = ColorRuleMetric.AccountWinrate, Operator = ColorRuleOperator.GreaterThan, Threshold = 0.4, RowBrushKey = "RowApeBrush" },
        };

        Assert.Equal("RowUnicumBrush", Evaluate(rules, winrate: 0.6));
    }

    [Fact]
    public void CustomMetricAndOperator()
    {
        var rules = new List<ColorRule>
        {
            new() { Metric = ColorRuleMetric.AccountAvgDamage, Operator = ColorRuleOperator.LessOrEqual, Threshold = 30000, RowBrushKey = "RowApeBrush" },
        };

        Assert.Equal("RowApeBrush", Evaluate(rules, avgDamage: 25000));
        Assert.Equal(string.Empty, Evaluate(rules, avgDamage: 50000));
    }

    [Theory]
    [InlineData(ColorRuleOperator.GreaterThan, 5, 5, false)]
    [InlineData(ColorRuleOperator.GreaterOrEqual, 5, 5, true)]
    [InlineData(ColorRuleOperator.LessThan, 5, 5, false)]
    [InlineData(ColorRuleOperator.LessOrEqual, 5, 5, true)]
    public void OperatorBoundaries(ColorRuleOperator op, double value, double threshold, bool expected)
    {
        Assert.Equal(expected, ColorRuleEngine.Matches(op, value, threshold));
    }

    [Fact]
    public void EmptyBrushKey_NotApplied()
    {
        var rules = new List<ColorRule>
        {
            new() { Metric = ColorRuleMetric.AccountWinrate, Operator = ColorRuleOperator.GreaterThan, Threshold = 0.5, RowBrushKey = string.Empty },
        };

        Assert.Equal(string.Empty, Evaluate(rules, winrate: 0.9));
    }
}
