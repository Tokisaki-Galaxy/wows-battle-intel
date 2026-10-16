namespace WowsBattleIntel.Core.Scoring;

/// <summary>
/// 名单着色的可判定指标。
/// </summary>
/// <remarks>
/// 每个指标对应玩家记录上的一个数值字段，供自定义染色规则按阈值判定。
/// 未知值用 -1 哨兵，规则求值时跳过，不参与判定，避免把隐藏战绩误判。
/// </remarks>
public enum ColorRuleMetric
{
    /// <summary>账号总胜率，0 到 1。</summary>
    AccountWinrate,

    /// <summary>加权胜率，0 到 1。</summary>
    WeightedWinrate,

    /// <summary>账号总场次。</summary>
    AccountBattles,

    /// <summary>账号场均伤害。</summary>
    AccountAvgDamage,

    /// <summary>本局所开船只的账号胜率，0 到 1。</summary>
    ShipWinrate,

    /// <summary>本局所开船只的账号场均伤害。</summary>
    ShipAvgDamage,
}

/// <summary>
/// 染色规则的比较算子。
/// </summary>
public enum ColorRuleOperator
{
    /// <summary>大于。</summary>
    GreaterThan,

    /// <summary>大于等于。</summary>
    GreaterOrEqual,

    /// <summary>小于。</summary>
    LessThan,

    /// <summary>小于等于。</summary>
    LessOrEqual,
}

/// <summary>
/// 一条自定义染色规则。
/// </summary>
/// <remarks>
/// 形如「若 胜率 大于 0.58 则整行染紫」。规则只是数据，判定逻辑在
/// <see cref="ColorRuleEngine"/>，与界面无关，便于单测。
/// </remarks>
public sealed class ColorRule
{
    /// <summary>判定的指标。</summary>
    public ColorRuleMetric Metric { get; set; } = ColorRuleMetric.AccountWinrate;

    /// <summary>比较算子。</summary>
    public ColorRuleOperator Operator { get; set; } = ColorRuleOperator.GreaterThan;

    /// <summary>阈值。胜率类指标用 0 到 1 的小数，与内部口径一致。</summary>
    public double Threshold { get; set; }

    /// <summary>
    /// 命中后的整行底色画刷资源键。空表示不染色。
    /// </summary>
    /// <remarks>
    /// 由配色决定，界面不暴露。默认两条规则分别是紫与红，对应用户常说的
    /// 紫色大佬与红色海猴。
    /// </remarks>
    public string RowBrushKey { get; set; } = string.Empty;
}

/// <summary>
/// 内置的默认染色规则。
/// </summary>
/// <remarks>
/// 紫色大佬与红色海猴两条，沿用应用既有口径：胜率高于 0.58 染紫、
/// 低于等于 0.47 染红。紫规则在前，高胜率优先判紫。配置缺失或为空时回落。
/// </remarks>
public static class DefaultColorRules
{
    /// <summary>默认规则集合。</summary>
    public static IReadOnlyList<ColorRule> Value { get; } =
    [
        new ColorRule
        {
            Metric = ColorRuleMetric.AccountWinrate,
            Operator = ColorRuleOperator.GreaterThan,
            Threshold = 0.58,
            RowBrushKey = "RowUnicumBrush",
        },
        new ColorRule
        {
            Metric = ColorRuleMetric.AccountWinrate,
            Operator = ColorRuleOperator.LessOrEqual,
            Threshold = 0.47,
            RowBrushKey = "RowApeBrush",
        },
    ];

    /// <summary>复制一份可变列表，供配置初始化与编辑。</summary>
    public static List<ColorRule> Clone() =>
        Value.Select(r => new ColorRule
        {
            Metric = r.Metric,
            Operator = r.Operator,
            Threshold = r.Threshold,
            RowBrushKey = r.RowBrushKey,
        }).ToList();
}

/// <summary>
/// 染色规则求值。
/// </summary>
/// <remarks>
/// 规则按顺序求值，第一条命中的决定整行颜色，后面的不再看。这个口径简单
/// 可预期：把紫规则放在红规则之前，高胜率玩家就会判紫，不受后面红规则影响。
///
/// 指标取值为 -1 哨兵（未知或隐藏）时该条规则不命中。这样隐藏战绩玩家不会
/// 因为默认的 0 值被误染。
/// </remarks>
public static class ColorRuleEngine
{
    /// <summary>
    /// 取指标在给定取值集合上的当前值。-1 表示未知。
    /// </summary>
    public static double ValueOf(
        ColorRuleMetric metric,
        double accountWinrate,
        double weightedWinrate,
        double accountBattles,
        double accountAvgDamage,
        double shipWinrate,
        double shipAvgDamage) => metric switch
        {
            ColorRuleMetric.AccountWinrate => accountWinrate,
            ColorRuleMetric.WeightedWinrate => weightedWinrate,
            ColorRuleMetric.AccountBattles => accountBattles,
            ColorRuleMetric.AccountAvgDamage => accountAvgDamage,
            ColorRuleMetric.ShipWinrate => shipWinrate,
            ColorRuleMetric.ShipAvgDamage => shipAvgDamage,
            _ => -1,
        };

    /// <summary>
    /// 按规则顺序求值，返回第一条命中规则的画刷键；都不命中返回空串。
    /// </summary>
    /// <remarks>
    /// 未知指标值（-1 哨兵）的规则直接跳过。
    /// </remarks>
    public static string Evaluate(
        IReadOnlyList<ColorRule> rules,
        double accountWinrate,
        double weightedWinrate,
        double accountBattles,
        double accountAvgDamage,
        double shipWinrate,
        double shipAvgDamage)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (var rule in rules)
        {
            double value = ValueOf(
                rule.Metric, accountWinrate, weightedWinrate, accountBattles,
                accountAvgDamage, shipWinrate, shipAvgDamage);
            if (value < 0)
            {
                continue;
            }

            if (Matches(rule.Operator, value, rule.Threshold) && rule.RowBrushKey.Length > 0)
            {
                return rule.RowBrushKey;
            }
        }

        return string.Empty;
    }

    /// <summary>单个算子判定。</summary>
    public static bool Matches(ColorRuleOperator op, double value, double threshold) => op switch
    {
        ColorRuleOperator.GreaterThan => value > threshold,
        ColorRuleOperator.GreaterOrEqual => value >= threshold,
        ColorRuleOperator.LessThan => value < threshold,
        ColorRuleOperator.LessOrEqual => value <= threshold,
        _ => false,
    };
}
