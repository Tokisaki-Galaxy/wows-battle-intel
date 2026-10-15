using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 单个玩家的展示模型。
/// </summary>
/// <remarks>
/// 把领域模型转成界面友好的形式，附带显示用的格式化文本与颜色标记。
/// </remarks>
public sealed class PlayerRowViewModel : ViewModelBase
{
    // 场均伤害粗分档位。PotatoAlert 按舰种与服务器分位值精化着色，本应用先用
    // 固定阈值粗分，后续可接服务器分位值再细化。
    private const double DamageDangerThreshold = 30000;
    private const double DamageOkThreshold = 60000;
    private const double DamageUnicumThreshold = 90000;

    // 账号场次低于此值判定样本不稳，标提醒色。
    private const double LowBattlesThreshold = 1000;

    private PlayerRecord? source;

    /// <summary>
    /// 从领域模型构造。
    /// </summary>
    public static PlayerRowViewModel FromPlayer(PlayerRecord player)
    {
        ArgumentNullException.ThrowIfNull(player);

        var vm = new PlayerRowViewModel { source = player };

        // 组队标记：单排不显示，双排显示 2，三排显示 3。
        vm.DivisionMark = player.DivisionSize switch
        {
            2 => "双排",
            3 => "三排",
            _ => string.Empty,
        };

        vm.WinrateText = player.AccountWinrate >= 0
            ? player.AccountWinrate.ToString("P1")
            : "隐藏";

        vm.BattlesText = player.AccountBattles >= 0
            ? ((int)player.AccountBattles).ToString("N0")
            : "-";

        vm.ShipText = string.IsNullOrEmpty(player.ShipName)
            ? player.ShipId
            : player.ShipName;

        vm.ShipTypeText = $"{ShipClassLabel(player.ShipClass)} {player.ShipTier}";

        // 加权胜率与罗马数字等级在读取阶段一次性格式化，避免列绑定重复计算。
        vm.WeightedWinrateText = player.WeightedWinrate >= 0
            ? player.WeightedWinrate.ToString("P1")
            : "-";
        vm.TierRoman = ToRoman(player.ShipTier);

        return vm;
    }

    /// <summary>昵称。</summary>
    public string PlayerName => source?.PlayerName ?? string.Empty;

    /// <summary>公会标签。</summary>
    public string ClanTag => source?.ClanTag ?? string.Empty;

    /// <summary>船名。</summary>
    public string ShipText { get; private set; } = string.Empty;

    /// <summary>舰种与等级。</summary>
    public string ShipTypeText { get; private set; } = string.Empty;

    /// <summary>加权胜率显示文本，未知显示 "-"。</summary>
    public string WeightedWinrateText { get; private set; } = string.Empty;

    /// <summary>舰船等级的罗马数字写法，11 级以上保持阿拉伯数字。</summary>
    public string TierRoman { get; private set; } = string.Empty;

    /// <summary>舰种枚举，行样式按舰种着底色用。</summary>
    public ShipClass ShipClass => source?.ShipClass ?? ShipClass.Unknown;

    /// <summary>组队人数，1 为单排。</summary>
    public int DivisionSize => source?.DivisionSize ?? 1;

    /// <summary>昵称完整形态：公会标签与昵称拼一段，ApeRadar 式单行展示。</summary>
    public string PlayerLineText
    {
        get
        {
            string name = source?.PlayerName ?? string.Empty;
            string tag = source?.ClanTag ?? string.Empty;
            return string.IsNullOrEmpty(tag) ? name : $"[{tag}] {name}";
        }
    }

    /// <summary>舰船完整形态：舰种缩写、罗马数字等级与船名拼一段，如 "DD X 君权"。</summary>
    public string ShipLineText
    {
        get
        {
            string ship = source is null
                ? string.Empty
                : string.IsNullOrEmpty(source.ShipName) ? source.ShipId : source.ShipName;
            string prefix = $"{ShipClassLabel(ShipClass)} {TierRoman}".Trim();
            return string.IsNullOrEmpty(prefix) ? ship : $"{prefix} {ship}".TrimEnd();
        }
    }

    /// <summary>胜率显示文本。</summary>
    public string WinrateText { get; private set; } = string.Empty;

    /// <summary>场次显示文本。</summary>
    public string BattlesText { get; private set; } = string.Empty;

    /// <summary>组队标记。</summary>
    public string DivisionMark { get; private set; } = string.Empty;

    /// <summary>是否隐藏战绩。</summary>
    public bool IsHidden => source?.IsHidden ?? false;

    /// <summary>是否机器人。</summary>
    public bool IsBot => source?.IsBot ?? false;

    /// <summary>
    /// 水平分类标签。
    /// </summary>
    public string TierLabel
    {
        get
        {
            if (source is null || !source.HasStats)
            {
                return "隐藏";
            }

            double wr = source.AccountWinrate;
            // 阈值沿用 ApeRadar 默认口径。
            if (wr > 0.58 && source.AccountBattles >= 500)
            {
                return "海王";
            }

            return wr <= 0.47 ? "猴子" : "普通";
        }
    }

    /// <summary>
    /// 水平分类的颜色资源键。
    /// </summary>
    /// <remarks>
    /// 海王用金色、猴子用紫色，普通与隐藏战绩用灰。具体色值由主题资源决定，
    /// 这里只给语义键名，经 BrushKeyConverter 换成画刷。
    /// </remarks>
    public string TierBrushKey => TierLabel switch
    {
        "海王" => "UnicumBrush",
        "猴子" => "ApeBrush",
        _ => "HiddenBrush",
    };

    /// <summary>
    /// 胜率列的颜色资源键。高低两端给语义色，中间保持默认文本色。
    /// </summary>
    public string WinrateBrushKey => WinrateBrushKeyFor(source?.AccountWinrate ?? -1, source?.AccountBattles ?? -1, requireHasStats: true);

    /// <summary>
    /// 胜率类着色统一口径：海王金、猴子紫、中间主文本色，未知灰。
    /// requireHasStats 用于账号胜率，隐藏战绩玩家没有快照，与未知区分口径一致。
    /// </summary>
    private string WinrateBrushKeyFor(double winrate, double battles, bool requireHasStats = false)
    {
        if (winrate < 0 || (requireHasStats && source is not null && !source.HasStats))
        {
            return "HiddenBrush";
        }

        if (winrate > 0.58 && battles >= 500)
        {
            return "UnicumBrush";
        }

        return winrate <= 0.47 ? "ApeBrush" : "TextPrimaryBrush";
    }

    /// <summary>
    /// 本局伤害显示。
    /// </summary>
    public string DamageText => source is null
        ? "-"
        : source.DamageDealt > 0 ? source.DamageDealt.ToString("N0") : "-";

    /// <summary>
    /// 本局击杀显示。
    /// </summary>
    public string KillsText => source is null || source.Kills == 0
        ? "-"
        : source.Kills.ToString();

    /// <summary>账号场均伤害显示，未知显示 "-"。</summary>
    public string AvgDamageText => source is null || source.AccountAvgDamage < 0
        ? "-"
        : ((int)source.AccountAvgDamage).ToString("N0");

    /// <summary>
    /// 账号场均伤害的颜色资源键。按固定阈值粗分档位，低伤害标危险色，
    /// 中段保持主文本色，高伤害给良与优的强调色。
    /// </summary>
    public string AvgDamageBrushKey => DamageBrushKey(source?.AccountAvgDamage ?? -1);

    /// <summary>本局船的场次显示，未知显示 "-"。</summary>
    public string ShipBattlesText => source is null || source.ShipBattles < 0
        ? "-"
        : ((int)source.ShipBattles).ToString("N0");

    /// <summary>本局船的胜率显示，未知显示 "-"。</summary>
    public string ShipWinrateText => source is null || source.ShipWinrate < 0
        ? "-"
        : source.ShipWinrate.ToString("P1");

    /// <summary>
    /// 本局船胜率的颜色资源键。与账号胜率同一套口径：海王金、猴子紫、
    /// 中间保持主文本色。场次不足的海王判定沿用到单船。
    /// </summary>
    public string ShipWinrateBrushKey => WinrateBrushKeyFor(source?.ShipWinrate ?? -1, source?.ShipBattles ?? -1);

    /// <summary>
    /// 加权胜率的颜色资源键。与账号胜率同一套口径，场次门槛沿用账号场次。
    /// </summary>
    public string WeightedWinrateBrushKey => WinrateBrushKeyFor(source?.WeightedWinrate ?? -1, source?.AccountBattles ?? -1);

    /// <summary>本局船的场均伤害显示，未知显示 "-"。</summary>
    public string ShipDamageText => source is null || source.ShipAvgDamage < 0
        ? "-"
        : ((int)source.ShipAvgDamage).ToString("N0");

    /// <summary>
    /// 本局船场均伤害的颜色资源键。与账号场均伤害同一套粗分阈值。
    /// </summary>
    public string ShipDamageBrushKey => DamageBrushKey(source?.ShipAvgDamage ?? -1);

    /// <summary>
    /// 账号场次的颜色资源键。场次过少时样本不稳，标提醒色，其余保持主文本色。
    /// </summary>
    public string BattlesBrushKey
    {
        get
        {
            if (source is null || source.AccountBattles < 0)
            {
                return "TextSecondaryBrush";
            }

            return source.AccountBattles < LowBattlesThreshold
                ? "WarnBrush"
                : "TextPrimaryBrush";
        }
    }

    /// <summary>
    /// 场均伤害粗分着色。未知保持主文本色，其余按固定阈值分四档。
    /// </summary>
    private static string DamageBrushKey(double avgDamage)
    {
        if (avgDamage < 0)
        {
            return "TextSecondaryBrush";
        }

        if (avgDamage < DamageDangerThreshold)
        {
            return "DangerBrush";
        }

        if (avgDamage < DamageOkThreshold)
        {
            return "TextPrimaryBrush";
        }

        return avgDamage < DamageUnicumThreshold ? "OkBrush" : "UnicumBrush";
    }

    private static string ShipClassLabel(ShipClass shipClass) => shipClass switch
    {
        ShipClass.Destroyer => "DD",
        ShipClass.Cruiser => "CA",
        ShipClass.Battleship => "BB",
        ShipClass.Carrier => "CV",
        ShipClass.Submarine => "SS",
        _ => "??",
    };

    /// <summary>
    /// 名单排序用的舰种次序：航母、战列、巡洋、驱逐、潜艇，未知垫底。
    /// 与 ApeRadar 的舰种分组顺序一致。
    /// </summary>
    public static int ShipClassSortKey(ShipClass shipClass) => shipClass switch
    {
        ShipClass.Carrier => 0,
        ShipClass.Battleship => 1,
        ShipClass.Cruiser => 2,
        ShipClass.Destroyer => 3,
        ShipClass.Submarine => 4,
        _ => 5,
    };

    /// <summary>等级转罗马数字。超出一到十的范围回退阿拉伯数字。</summary>
    private static string ToRoman(int tier) => tier switch
    {
        1 => "I",
        2 => "II",
        3 => "III",
        4 => "IV",
        5 => "V",
        6 => "VI",
        7 => "VII",
        8 => "VIII",
        9 => "IX",
        10 => "X",
        11 => "XI",
        _ => tier > 0 ? tier.ToString() : string.Empty,
    };
}
