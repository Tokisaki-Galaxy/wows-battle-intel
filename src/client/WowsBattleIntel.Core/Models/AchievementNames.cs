namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 战斗成就的英文名到中文名对照。
/// </summary>
/// <remarks>
/// 回放里给的是成就内部名与英文展示名，界面上直接显示英文和其余列的中文
/// 风格不一致。这里做一层静态对照，未收录的成就保持原文，避免翻错。
///
/// 译名采用国服官方口径：Dreadnought 无畏者、High Caliber 技高一筹、
/// Kraken Unleashed 海怪出没等。新版本新增成就时补这张表即可。
/// </remarks>
public static class AchievementNames
{
    private static readonly Dictionary<string, string> Chinese = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dreadnought"] = "无畏者",
        ["First Blood"] = "第一滴血",
        ["Devastating Strike"] = "致命打击",
        ["General Offensive"] = "全面进攻",
        ["Confederate"] = "同盟",
        ["It's Just a Flesh Wound!"] = "不过是个开始！",
        ["Close Quarters Expert"] = "近战专家",
        ["Witherer"] = "虚弱",
        ["Fireproof"] = "耐火",
        ["Combat Scout"] = "战斗侦察",
        ["High Caliber"] = "技高一筹",
        ["AA Defense Expert"] = "防空专家",
        ["Brothers-in-Arms"] = "生死之交",
        ["Strike Team"] = "打击编队",
        ["Arsonist"] = "纵火犯",
        ["Double Strike"] = "双杀",
        ["Shoulder to Shoulder"] = "并肩作战",
        ["Kraken Unleashed!"] = "海怪出没！",
        ["Coordinated Attack"] = "协同攻击",
        ["Die-Hard"] = "坚固",
        ["Unsinkable"] = "不沉",
        ["Solo Warrior"] = "独行者",
        ["Liquidator"] = "清算",
        ["Clear Sky"] = "晴空",
        ["Detonation"] = "殉爆",
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

        string name = Chinese.TryGetValue(text, out string? chinese) ? chinese : text;
        return suffix.Length == 0 ? name : $"{name} {suffix}";
    }
}
