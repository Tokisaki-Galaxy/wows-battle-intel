using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 单条染色规则的编辑模型。
/// </summary>
/// <remarks>
/// 指标与算子用下拉下标绑定，阈值用文本框绑定。胜率类指标内部是 0 到 1 的
/// 小数，界面按百分数展示与录入，这里负责换算，用户输入 58 即 58%。
///
/// 颜色由规则序位决定：第一条紫、第二条红，对应紫色大佬与红色海猴，
/// 界面上作为固定标签展示，不提供选择。
/// </remarks>
public sealed class ColorRuleViewModel : ViewModelBase
{
    private int metricIndex;
    private int operatorIndex;
    private double threshold;

    /// <summary>
    /// 从领域规则构造。
    /// </summary>
    /// <param name="rule">来源规则。</param>
    /// <param name="colorLabel">界面展示的固定颜色标签。</param>
    public ColorRuleViewModel(ColorRule rule, string colorLabel)
    {
        ArgumentNullException.ThrowIfNull(rule);

        RowBrushKey = rule.RowBrushKey;
        ColorLabel = colorLabel;
        metricIndex = MetricToIndex(rule.Metric);
        operatorIndex = OperatorToIndex(rule.Operator);
        threshold = ToDisplay(rule.Metric, rule.Threshold);
    }

    /// <summary>整行底色资源键，界面不暴露，保存时原样写回。</summary>
    public string RowBrushKey { get; }

    /// <summary>固定的颜色标签，如"紫色"。</summary>
    public string ColorLabel { get; }

    /// <summary>指标下拉下标。</summary>
    public int MetricIndex
    {
        get => metricIndex;
        set => SetField(ref metricIndex, value);
    }

    /// <summary>算子下拉下标。</summary>
    public int OperatorIndex
    {
        get => operatorIndex;
        set => SetField(ref operatorIndex, value);
    }

    /// <summary>阈值。胜率类指标在界面上以百分数展示。</summary>
    public double Threshold
    {
        get => threshold;
        set => SetField(ref threshold, value);
    }

    /// <summary>当前指标。</summary>
    public ColorRuleMetric Metric => IndexToMetric(MetricIndex);

    /// <summary>当前算子。</summary>
    public ColorRuleOperator Operator => IndexToOperator(OperatorIndex);

    /// <summary>
    /// 组装回领域规则，阈值从百分数（胜率类）换回 0 到 1 的小数。
    /// </summary>
    public ColorRule ToRule() => new()
    {
        Metric = Metric,
        Operator = Operator,
        Threshold = FromDisplay(Metric, Threshold),
        RowBrushKey = RowBrushKey,
    };

    /// <summary>胜率类指标，界面上以百分数呈现。</summary>
    public static bool IsWinrateMetric(ColorRuleMetric metric) =>
        metric is ColorRuleMetric.AccountWinrate
            or ColorRuleMetric.WeightedWinrate
            or ColorRuleMetric.ShipWinrate;

    /// <summary>领域值换算成界面展示值。</summary>
    public static double ToDisplay(ColorRuleMetric metric, double value) =>
        IsWinrateMetric(metric) ? value * 100 : value;

    /// <summary>界面展示值换算回领域值。</summary>
    public static double FromDisplay(ColorRuleMetric metric, double value) =>
        IsWinrateMetric(metric) ? value / 100 : value;

    /// <summary>指标枚举转下拉下标，与界面顺序一一对应。</summary>
    public static int MetricToIndex(ColorRuleMetric metric) => metric switch
    {
        ColorRuleMetric.AccountWinrate => 0,
        ColorRuleMetric.WeightedWinrate => 1,
        ColorRuleMetric.AccountBattles => 2,
        ColorRuleMetric.AccountAvgDamage => 3,
        ColorRuleMetric.ShipWinrate => 4,
        ColorRuleMetric.ShipAvgDamage => 5,
        _ => 0,
    };

    /// <summary>下拉下标转指标枚举。</summary>
    public static ColorRuleMetric IndexToMetric(int index) => index switch
    {
        1 => ColorRuleMetric.WeightedWinrate,
        2 => ColorRuleMetric.AccountBattles,
        3 => ColorRuleMetric.AccountAvgDamage,
        4 => ColorRuleMetric.ShipWinrate,
        5 => ColorRuleMetric.ShipAvgDamage,
        _ => ColorRuleMetric.AccountWinrate,
    };

    /// <summary>算子枚举转下拉下标，与界面顺序一一对应。</summary>
    public static int OperatorToIndex(ColorRuleOperator op) => op switch
    {
        ColorRuleOperator.GreaterThan => 0,
        ColorRuleOperator.GreaterOrEqual => 1,
        ColorRuleOperator.LessThan => 2,
        ColorRuleOperator.LessOrEqual => 3,
        _ => 0,
    };

    /// <summary>下拉下标转算子枚举。</summary>
    public static ColorRuleOperator IndexToOperator(int index) => index switch
    {
        1 => ColorRuleOperator.GreaterOrEqual,
        2 => ColorRuleOperator.LessThan,
        3 => ColorRuleOperator.LessOrEqual,
        _ => ColorRuleOperator.GreaterThan,
    };
}
