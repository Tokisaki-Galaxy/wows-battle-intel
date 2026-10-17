namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 战斗成就的英文名到中文名对照。
/// </summary>
/// <remarks>
/// 回放里给的是成就内部名与英文展示名，界面上直接显示英文和其余列的中文
/// 风格不一致。这里做一层静态对照，未收录的成就保持原文，避免翻错。
///
/// 译名取自世界服客户端简中文本（zh_sg），即亚服游戏内实际显示的名字，
/// 不是国服译名。两者差异不小：国服把 High Caliber 译作「技高一筹」，
/// 亚服是「重创」；国服「无畏者」对应亚服「无人能挡」。改动这张表时请
/// 以亚服为准，不要凭印象补国服译名。
///
/// 数据来源：世界服客户端解包文本 IDS_ACHIEVEMENT_* 与 zh_sg 字段。
///
/// 新版本新增成就时补这张表即可，查不到时保留英文原文。
/// </remarks>
public static class AchievementNames
{
    // 键用亚服英文展示名。注意回放里的写法可能不带叹号或标点略有出入，
    // 命中时另走 NormalizeName 做一次宽松匹配。
    private static readonly Dictionary<string, string> Chinese = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dreadnought"] = "无人能挡",
        ["First Blood"] = "第一滴血",
        ["Devastating Strike"] = "毁灭打击",
        ["General Offensive"] = "全面进攻",
        ["Confederate"] = "支援",
        ["It's Just a Flesh Wound!"] = "轻伤不下火线！",
        ["Close Quarters Expert"] = "近战专家",
        ["Witherer"] = "天灾人祸",
        ["Fireproof"] = "耐火专精",
        ["Combat Scout"] = "侦察尖兵",
        ["High Caliber"] = "重创",
        ["AA Defense Expert"] = "对空防御专家",
        ["Brothers-in-Arms"] = "兄弟连",
        ["Strike Team"] = "打击小队",
        ["Arsonist"] = "纵火犯",
        ["Double Strike"] = "一炮双响",
        ["Shoulder to Shoulder"] = "并肩奋战",
        ["Kraken Unleashed!"] = "海怪出没！",
        ["Coordinated Attack"] = "协同攻击",
        ["Die-Hard"] = "不死鸟",
        ["Unsinkable"] = "永不沉没",
        ["Solo Warrior"] = "以一当十",
        ["Liquidator"] = "进水专家",
        ["Clear Sky"] = "晴空万里",
        ["Detonation"] = "爆破手",
        ["Crucial Contribution"] = "杰出贡献",
        ["Invisible Threat"] = "隐藏的威胁",

        // 战争艺术系列，亚服按「战争艺术：」统一前缀，国服译作「海军战争」类。
        ["Naval Warfare: Weaponry Basics"] = "战争艺术：基础武器",
        ["Naval Warfare: Arson"] = "战争艺术：点火",
        ["Naval Warfare: Flooding"] = "战争艺术：进水",
        ["Naval Warfare: Ramming"] = "战争艺术：撞击",
        ["Naval Warfare: Lucky Shot"] = "战争艺术：幸运一击",
        ["Naval Warfare: Tactics"] = "战争艺术：战术大师",
    };

    /// <summary>
    /// 宽松匹配用的别名表。
    /// </summary>
    /// <remarks>
    /// 回放与游戏内的写法偶尔差一个感叹号或空格，例如 Kraken Unleashed 与
    /// Kraken Unleashed!。归一化之后两边能对上，避免漏译。
    ///
    /// 键必须是 NormalizeName 处理过的形态，即全小写且不含空格标点。
    /// </remarks>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["krakenunleashed"] = "Kraken Unleashed!",
        ["itsjustafleshwound"] = "It's Just a Flesh Wound!",
        ["brothersinarms"] = "Brothers-in-Arms",
        ["diehard"] = "Die-Hard",
        ["shouldertoshoulder"] = "Shoulder to Shoulder",
    };

    /// <summary>
    /// 把英文成就名翻成中文，未收录时原样返回。
    /// </summary>
    /// <remarks>
    /// 入参可能是「First Blood ×2」这种带数量的形态，拆出名字与后缀
    /// 分别处理，数量不能丢。
    /// </remarks>
    public static string ToChinese(string english)
    {
        if (string.IsNullOrWhiteSpace(english))
        {
            return english ?? string.Empty;
        }

        string text = english.Trim();
        string suffix = string.Empty;

        int times = text.LastIndexOf('×');
        if (times > 0)
        {
            suffix = text[times..];
            text = text[..times].TrimEnd();
        }

        string key = Aliases.TryGetValue(NormalizeName(text), out string? canonical)
            ? canonical
            : text;

        string name = Chinese.TryGetValue(key, out string? chinese) ? chinese : text;
        return suffix.Length == 0 ? name : $"{name} {suffix}";
    }

    /// <summary>
    /// 归一化成就名，用于宽松匹配：去掉标点与全部空白。
    /// </summary>
    /// <remarks>
    /// 回放文本与游戏内文本的差别多见为感叹号、撇号与空格，
    /// 这些都剥掉之后再比对，避免同一成就因为一个标点漏译。
    /// </remarks>
    private static string NormalizeName(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
