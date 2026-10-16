using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Scoring;
using WowsBattleIntel.Stats;

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

        var vm = new PlayerRowViewModel();
        vm.Apply(player);
        return vm;
    }

    /// <summary>
    /// 一键复制战绩命令。把本行玩家的战绩文本写入剪贴板。
    /// </summary>
    /// <remarks>
    /// 剪贴板可能被其他进程占用，SetText 抛异常时静默忽略，不打断界面。
    /// 复制内容由 <see cref="BuildBattleRecordText"/> 生成。
    /// </remarks>
    public System.Windows.Input.ICommand CopyBattleRecordCommand { get; }

    /// <summary>
    /// 构造视图模型，装配命令。命令的委托捕获本实例，必须在构造里建。
    /// </summary>
    private PlayerRowViewModel()
    {
        CopyBattleRecordCommand = new RelayCommand(_ =>
        {
            CopyBattleRecordToClipboard();
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 把本行战绩写入剪贴板。供命令调用，抽取出来便于单测。
    /// </summary>
    /// <returns>复制成功返回 true。</returns>
    public bool CopyBattleRecordToClipboard()
    {
        string text = BuildBattleRecordText();
        if (text.Length == 0)
        {
            return false;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (Exception)
        {
            // 剪贴板被占用等场景，静默失败。
            return false;
        }
    }

    /// <summary>
    /// 用新的领域模型刷新本行。
    /// </summary>
    /// <remarks>
    /// 渐进式补拉完成后增量更新行内容：行对象按引用持有在名单集合里，
    /// 这里重算全部格式化属性并逐项通知，不重建集合也不引起重排。
    /// </remarks>
    public void RefreshFrom(PlayerRecord player)
    {
        ArgumentNullException.ThrowIfNull(player);

        Apply(player);

        // 派生属性全部重新求值：可写字段经 Apply 幂等重算，派生只读属性
        // 依赖 source 的字段，这里统一通知一遍。
        OnPropertyChanged(nameof(PlayerName));
        OnPropertyChanged(nameof(ClanTag));
        OnPropertyChanged(nameof(ClanTagText));
        OnPropertyChanged(nameof(PlayerNameText));
        OnPropertyChanged(nameof(ClanTagBrushKey));
        OnPropertyChanged(nameof(HasClanTag));
        OnPropertyChanged(nameof(ClanToolTip));
        OnPropertyChanged(nameof(ShipText));
        OnPropertyChanged(nameof(ShipTypeText));
        OnPropertyChanged(nameof(WeightedWinrateText));
        OnPropertyChanged(nameof(TierRoman));
        OnPropertyChanged(nameof(ShipClass));
        OnPropertyChanged(nameof(DivisionSize));
        OnPropertyChanged(nameof(PlayerLineText));
        OnPropertyChanged(nameof(ShipLineText));
        OnPropertyChanged(nameof(WinrateText));
        OnPropertyChanged(nameof(BattlesText));
        OnPropertyChanged(nameof(DivisionMark));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsBot));
        OnPropertyChanged(nameof(TierLabel));
        OnPropertyChanged(nameof(TierBrushKey));
        OnPropertyChanged(nameof(RowBackgroundBrushKey));
        OnPropertyChanged(nameof(WinrateBrushKey));
        OnPropertyChanged(nameof(DamageText));
        OnPropertyChanged(nameof(KillsText));
        OnPropertyChanged(nameof(AvgDamageText));
        OnPropertyChanged(nameof(ClassBattlesText));
        OnPropertyChanged(nameof(ClassWinrateText));
        OnPropertyChanged(nameof(ClassDamageText));
        OnPropertyChanged(nameof(ClassMasteryBrushKey));
        OnPropertyChanged(nameof(AvgDamageBrushKey));
        OnPropertyChanged(nameof(ShipBattlesText));
        OnPropertyChanged(nameof(ShipWinrateText));
        OnPropertyChanged(nameof(ShipWinrateBrushKey));
        OnPropertyChanged(nameof(WeightedWinrateBrushKey));
        OnPropertyChanged(nameof(ShipDamageText));
        OnPropertyChanged(nameof(ShipDamageBrushKey));
        OnPropertyChanged(nameof(BattlesBrushKey));
    }

    /// <summary>
    /// 把领域模型换算成显示字段。构造与增量刷新共用，保证口径一致。
    /// </summary>
    private void Apply(PlayerRecord player)
    {
        source = player;

        // 组队标记：单排不显示，双排显示 2，三排显示 3。
        DivisionMark = player.DivisionSize switch
        {
            2 => "双排",
            3 => "三排",
            _ => string.Empty,
        };

        WinrateText = player.AccountWinrate >= 0
            ? player.AccountWinrate.ToString("P1")
            : "隐藏";

        BattlesText = player.AccountBattles >= 0
            ? ((int)player.AccountBattles).ToString("N0")
            : "-";

        // 临时名单的 ShipName 为空，先查船目录，再退回船 ID。
        ShipText = string.IsNullOrEmpty(player.ShipName)
            ? ShipCatalog.TryGetChineseName(player.ShipId, out string? preName) && preName is not null
                ? preName
                : player.ShipId
            : player.ShipName;

        ShipTypeText = $"{ShipClassLabel(player.ShipClass)} {player.ShipTier}";

        // 加权胜率与罗马数字等级在读取阶段一次性格式化，避免列绑定重复计算。
        WeightedWinrateText = player.WeightedWinrate >= 0
            ? player.WeightedWinrate.ToString("P1")
            : "-";
        TierRoman = ToRoman(player.ShipTier);
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

    /// <summary>
    /// 昵称完整形态：公会标签与昵称拼一段，ApeRadar 式单行展示。
    /// </summary>
    /// <remarks>
    /// 保留拼接属性供测试与日志使用，界面拆成 ClanTagText 与 PlayerNameText
    /// 两个绑定分别着色。
    /// </remarks>
    public string PlayerLineText
    {
        get
        {
            string name = PlayerNameText;
            string tag = ClanTagText;
            return tag.Length == 0 ? name : $"{tag} {name}";
        }
    }

    /// <summary>公会标签带方括号的展示形态，无标签为空串。</summary>
    public string ClanTagText
    {
        get
        {
            string tag = source?.ClanTag ?? string.Empty;
            if (tag.Length == 0)
            {
                return string.Empty;
            }

            return tag.Length >= 2 && tag.StartsWith("[") ? tag : $"[{tag}]";
        }
    }

    /// <summary>昵称展示形态。</summary>
    public string PlayerNameText => source?.PlayerName ?? string.Empty;

    /// <summary>
    /// 军团标签的颜色资源键，按军团分档着色。空串经 BrushKeyConverter
    /// 回落透明，配合 HasClanTag 可见性触发器隐藏占位。
    /// </summary>
    public string ClanTagBrushKey => source?.ClanColorKey ?? string.Empty;

    /// <summary>是否有军团标签可展示。</summary>
    public bool HasClanTag => ClanTagText.Length > 0;

    /// <summary>军团 ToolTip 的详情行，由补拉链路填充，空为未查询。</summary>
    private string? clanToolTipDetail;

    /// <summary>
    /// 军团 ToolTip：军团名、成员数与成员聚合胜率。
    /// </summary>
    /// <remarks>
    /// 分档数据来自军团目录在线查询，未知时只给标签与分档名。
    /// </remarks>
    public string ClanToolTip
    {
        get
        {
            string tag = ClanTagText;
            if (tag.Length == 0)
            {
                return string.Empty;
            }

            string tagLine = source?.ClanColorKey switch
            {
                "UnicumBrush" => $"{tag} 紫会",
                "PrimaryFgBrush" => $"{tag} 强会",
                _ => tag,
            };

            return clanToolTipDetail is null
                ? tagLine
                : $"{tagLine}\n{clanToolTipDetail}";
        }
    }

    /// <summary>
    /// 填充军团 ToolTip 详情。军团目录查询完成后调用。
    /// </summary>
    public void SetClanDetail(string detail)
    {
        if (string.IsNullOrEmpty(detail) || string.Equals(clanToolTipDetail, detail, StringComparison.Ordinal))
        {
            return;
        }

        clanToolTipDetail = detail;
        OnPropertyChanged(nameof(ClanToolTip));
    }

    /// <summary>舰船完整形态：舰种缩写、罗马数字等级与船名拼一段，如 "DD X 君权"。</summary>
    /// <remarks>
    /// 船名优先取词典中文，按数字船 ID 匹配。存量库里旧记录的 ship_id 是
    /// 内部索引码，词典查不到，保持英文船名回落。
    /// </remarks>
    public string ShipLineText
    {
        get
        {
            string ship;
            if (source is null)
            {
                ship = string.Empty;
            }
            else if (ShipCatalog.TryGetChineseName(source.ShipId, out string? chinese))
            {
                ship = chinese!;
            }
            else
            {
                ship = string.IsNullOrEmpty(source.ShipName) ? source.ShipId : source.ShipName;
            }

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
    /// 由用户配置的染色规则求值，默认紫大佬红色海猴，两条规则都未命中时用灰。
    /// 具体色值由主题资源决定，这里只给语义键名，经 BrushKeyConverter 换成画刷。
    /// </remarks>
    public string TierBrushKey => ColorRuleEngine.Evaluate(
        ColorRuleSet.Rules,
        source?.AccountWinrate ?? -1,
        source?.WeightedWinrate ?? -1,
        source?.AccountBattles ?? -1,
        source?.AccountAvgDamage ?? -1,
        source?.ShipWinrate ?? -1,
        source?.ShipAvgDamage ?? -1) switch
    {
        "RowUnicumBrush" => "UnicumBrush",
        "RowApeBrush" => "ApeBrush",
        _ => "HiddenBrush",
    };

    /// <summary>
    /// 整行底色的资源键。由染色规则直接给出，命中哪条用哪条的底色。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="TierBrushKey"/> 同源：规则命中紫则行底紫、文字也紫。
    /// 空串表示不染色，行背景保持透明。
    /// </remarks>
    public string RowBackgroundBrushKey => ColorRuleEngine.Evaluate(
        ColorRuleSet.Rules,
        source?.AccountWinrate ?? -1,
        source?.WeightedWinrate ?? -1,
        source?.AccountBattles ?? -1,
        source?.AccountAvgDamage ?? -1,
        source?.ShipWinrate ?? -1,
        source?.ShipAvgDamage ?? -1);

    /// <summary>
    /// 胜率列的颜色资源键。高低两端给语义色，中间保持默认文本色。
    /// </summary>
    public string WinrateBrushKey => WinrateBrushKeyFor(source?.AccountWinrate ?? -1, source?.AccountBattles ?? -1, requireHasStats: true);

    /// <summary>
    /// 胜率类着色统一口径：海王紫、猴子红、中间主文本色，未知灰。
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

    /// <summary>账号场均伤害显示。带常用等级前缀，如 "(X) 34,444"，未知显示 "-"。</summary>
    public string AvgDamageText => source is null || source.AccountAvgDamage < 0
        ? "-"
        : TierPrefix is null
            ? ((int)source.AccountAvgDamage).ToString("N0")
            : $"{TierPrefix} {((int)source.AccountAvgDamage).ToString("N0")}";

    /// <summary>常用等级的罗马数字前缀，如 "(X)"，未知为 null。</summary>
    private string? TierPrefix => FavoriteTierRoman is null
        ? null
        : $"({FavoriteTierRoman})";

    /// <summary>账号最常玩等级的罗马数字，未知为 null。</summary>
    private string? FavoriteTierRoman => source is null || source.FavoriteTier < 1
        ? null
        : ToRoman(source.FavoriteTier);

    /// <summary>
    /// 舰种熟练显示：本局舰种的账号场次、胜率、场均伤害三行。
    /// 分舰种数据缺失显示 "-"。
    /// </summary>
    public string ClassBattlesText
    {
        get
        {
            if (source is null || source.ClassBattles < 0)
            {
                return "-";
            }

            return ((int)source.ClassBattles).ToString("N0");
        }
    }

    /// <summary>本局舰种的账号胜率显示，未知显示 "-"。</summary>
    public string ClassWinrateText
    {
        get
        {
            if (source is null || source.ClassBattles <= 0 || source.ClassWins < 0)
            {
                return "-";
            }

            return (source.ClassWins / source.ClassBattles).ToString("P1");
        }
    }

    /// <summary>本局舰种的账号场均伤害显示，伤害维度缺失显示 "-"。</summary>
    public string ClassDamageText
    {
        get
        {
            if (source is null || source.ClassBattles <= 0 || source.ClassDamage < 0)
            {
                return "-";
            }

            return ((int)(source.ClassDamage / source.ClassBattles)).ToString("N0");
        }
    }

    /// <summary>
    /// 舰种熟练的颜色资源键。与胜率同一套口径，海王紫、猴子红、中间主文本。
    /// </summary>
    public string ClassMasteryBrushKey
    {
        get
        {
            if (source is null || source.ClassBattles <= 0 || source.ClassWins < 0)
            {
                return "TextSecondaryBrush";
            }

            return WinrateBrushKeyFor(source.ClassWins / source.ClassBattles, source.ClassBattles);
        }
    }

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
    /// 本局船胜率的颜色资源键。与账号胜率同一套口径：海王紫、猴子红、
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

    /// <summary>
    /// 生成一键复制用的战绩文本。
    /// </summary>
    /// <remarks>
    /// 形如「[ABC] 玩家名（船名），账号5000场 58.00%胜率 场均34567 最常用等级X，
    /// 舰种1234场 56.78%胜率，单船200场 60.00%胜率 45000场均。」。缺失项用 "-"
    /// 占位，保持模板结构不塌。舰种取本局所开船的舰种，与名单舰种列口径一致。
    /// </remarks>
    public string BuildBattleRecordText()
    {
        if (source is null)
        {
            return string.Empty;
        }

        string clanTag = ClanTagText;
        string namePart = clanTag.Length == 0 ? PlayerName : $"{clanTag} {PlayerName}";
        string shipName = ShipLineText.Length > 0 ? ShipLineText : ShipText;

        string battles = source.AccountBattles >= 0
            ? ((int)source.AccountBattles).ToString("N0")
            : "-";
        string winrate = source.AccountWinrate >= 0
            ? source.AccountWinrate.ToString("P2")
            : "-";
        string avgDamage = source.AccountAvgDamage >= 0
            ? ((int)source.AccountAvgDamage).ToString("N0")
            : "-";
        string favoriteTier = FavoriteTierRoman ?? "-";

        string classBattles = source.ClassBattles >= 0
            ? ((int)source.ClassBattles).ToString("N0")
            : "-";
        string classWinrate = source.ClassBattles > 0 && source.ClassWins >= 0
            ? (source.ClassWins / source.ClassBattles).ToString("P2")
            : "-";

        string shipBattles = source.ShipBattles >= 0
            ? ((int)source.ShipBattles).ToString("N0")
            : "-";
        string shipWinrate = source.ShipWinrate >= 0
            ? source.ShipWinrate.ToString("P2")
            : "-";
        string shipAvgDamage = source.ShipAvgDamage >= 0
            ? ((int)source.ShipAvgDamage).ToString("N0")
            : "-";

        return $"{namePart}（{shipName}），账号{battles}场 {winrate}胜率 场均{avgDamage}"
            + $" 最常用等级{favoriteTier}，舰种{classBattles}场 {classWinrate}胜率，"
            + $"单船{shipBattles}场 {shipWinrate}胜率 {shipAvgDamage}场均。";
    }
}
