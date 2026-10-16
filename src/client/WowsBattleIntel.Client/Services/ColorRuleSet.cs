using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 当前生效的名单染色规则。
/// </summary>
/// <remarks>
/// 规则来自用户配置，是全局单例性质的数据：所有玩家行共用同一套。视图模型层
/// 没有依赖注入，用这个进程级持有者在配置加载与保存时更新，玩家行构造时读取。
/// 与 ArenaInfoParser.ShipMetaLookup、ThemeService 的做法一致，应用是单实例
/// 桌面程序，不需要更重的机制。
///
/// 默认值为内置的紫色大佬与红色海猴两条规则，保证配置未加载时行色不缺失。
/// 传入的规则会复制一份持有，调用方后续改动自己的列表不会影响全局。
/// </remarks>
public static class ColorRuleSet
{
    private static IReadOnlyList<ColorRule> rules = DefaultColorRules.Value;

    /// <summary>当前规则。按顺序求值，第一条命中的决定行色。</summary>
    public static IReadOnlyList<ColorRule> Rules => rules;

    /// <summary>
    /// 替换当前规则。传 null 或空列表时回落默认规则。
    /// </summary>
    public static void Set(IReadOnlyList<ColorRule>? value)
    {
        rules = value is null || value.Count == 0
            ? DefaultColorRules.Value
            : value.Select(CloneRule).ToList();
    }

    /// <summary>复制一条规则，切断与调用方列表的引用关系。</summary>
    private static ColorRule CloneRule(ColorRule rule) => new()
    {
        Metric = rule.Metric,
        Operator = rule.Operator,
        Threshold = rule.Threshold,
        RowBrushKey = rule.RowBrushKey,
    };
}
