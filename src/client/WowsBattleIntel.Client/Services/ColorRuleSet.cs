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
/// </remarks>
public static class ColorRuleSet
{
    private static IReadOnlyList<ColorRule> rules = DefaultColorRules.Value;

    /// <summary>当前规则。按顺序求值，第一条命中的决定行色。</summary>
    public static IReadOnlyList<ColorRule> Rules => rules;

    /// <summary>
    /// 替换当前规则。传空或全空键时回落默认规则。
    /// </summary>
    public static void Set(IReadOnlyList<ColorRule>? value)
    {
        rules = value is null || value.Count == 0
            ? DefaultColorRules.Value
            : value;
    }
}
