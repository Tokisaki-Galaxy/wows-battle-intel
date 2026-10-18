using System.Globalization;
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

    /// <summary>隐藏战绩的展示标签。与未知占位符区分，只在确认隐藏时出现。</summary>
    private const string HiddenLabel = "隐藏";

    /// <summary>默认的网页打开器。测试里替换成记录调用的假实现。</summary>
    private static readonly Services.IWebLauncher DefaultWebLauncher = new Services.ShellWebLauncher();

    private PlayerRecord? source;

    /// <summary>单玩家数据刷新回调。宿主（详情区）在构造行后注入。</summary>
    internal Func<PlayerRecord, Task>? StatsRefreshRequested;

    /// <summary>网页打开器。跳转详细记录时用，测试里替换。</summary>
    internal Services.IWebLauncher WebLauncher { get; set; } = DefaultWebLauncher;

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
    /// 刷新本行玩家的 WG 统计数据命令。
    /// </summary>
    public System.Windows.Input.ICommand RefreshStatsCommand { get; }

    /// <summary>
    /// 跳转本行玩家的详细记录页命令。
    /// </summary>
    public System.Windows.Input.ICommand OpenDetailedRecordCommand { get; }

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

        RefreshStatsCommand = new RelayCommand(
            async _ =>
            {
                if (source is { } player)
                {
                    await (StatsRefreshRequested?.Invoke(player) ?? Task.CompletedTask)
                        .ConfigureAwait(true);
                }
            });

        OpenDetailedRecordCommand = new RelayCommand(_ =>
        {
            // 账号未解析或服务器未知时拼不出链接，静默跳过比打开一个错误页好。
            string url = BuildDetailedRecordUrl();
            if (url.Length > 0)
            {
                WebLauncher.Open(url);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 拼出本行玩家在详细记录站的网址。
    /// </summary>
    /// <remarks>
    /// 站点按账号 ID 与昵称定位玩家，服务器决定库源。账号未解析（哨兵 -1）
    /// 或服务器未知时返回空串，命令侧静默跳过，不给用户一个 404 页。
    /// </remarks>
    public string BuildDetailedRecordUrl()
    {
        if (source is not { } player
            || player.AccountId.Length == 0
            || player.AccountId == "-1"
            || player.Server == GameServer.Auto)
        {
            return string.Empty;
        }

        // 站点参数里的服务器用小写缩写，与 realm 口径一致。
        string server = player.Server switch
        {
            GameServer.Asia => "asia",
            GameServer.Eu => "eu",
            GameServer.Na => "na",
            GameServer.Ru => "ru",
            GameServer.Cn => "cn",
            _ => string.Empty,
        };
        if (server.Length == 0)
        {
            return string.Empty;
        }

        return $"https://wows.shinoaki.com/#record?server={server}"
            + $"&accountId={Uri.EscapeDataString(player.AccountId)}"
            + $"&name={Uri.EscapeDataString(player.PlayerName)}&tab=info";
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
        OnPropertyChanged(nameof(DivisionId));
        OnPropertyChanged(nameof(SpottingText));
        OnPropertyChanged(nameof(TakenText));
        OnPropertyChanged(nameof(DamageTargetCount));
        OnPropertyChanged(nameof(DamageTargetsText));
        OnPropertyChanged(nameof(DamageTakenByText));
        OnPropertyChanged(nameof(AchievementCount));
        OnPropertyChanged(nameof(AchievementsText));
        OnPropertyChanged(nameof(AchievementBadgeText));
        OnPropertyChanged(nameof(ResultMark));
        OnPropertyChanged(nameof(HasKills));
        OnPropertyChanged(nameof(KillText));
        OnPropertyChanged(nameof(PlayerLineText));
        OnPropertyChanged(nameof(ShipLineText));
        OnPropertyChanged(nameof(WinrateText));
        OnPropertyChanged(nameof(BattlesText));
        OnPropertyChanged(nameof(DivisionMark));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsBot));
        OnPropertyChanged(nameof(TierLabel));
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

        // 未拉到数据与隐藏战绩是两件事：只有提供商确认隐藏才显示"隐藏"，
        // 其余未知一律占位符，避免补拉前后两种状态在界面上混成一种。
        WinrateText = player.AccountWinrate >= 0
            ? player.AccountWinrate.ToString("P1")
            : player.IsHidden ? HiddenLabel : "-";

        BattlesText = player.AccountBattles >= 0
            ? ((int)player.AccountBattles).ToString("N0")
            : "-";

        // 船名统一走船目录：数字 ID 优先，英文名反查兜底。
        // 早先这里英文优先、ShipLineText 中文优先，两个属性口径不一致，
        // 复制出战绩文本时会突然变成英文。
        ShipText = ShipCatalog.Resolve(player.ShipId, player.ShipName);

        ShipTypeText = $"{ShipClassLabel(player.ShipClass)} {player.ShipTier}";

        // 加权胜率与罗马数字等级在读取阶段一次性格式化，避免列绑定重复计算。
        WeightedWinrateText = player.WeightedWinrate >= 0
            ? player.WeightedWinrate.ToString("P1")
            : "-";
        TierRoman = ToRoman(player.ShipTier);

        ApplyBattlePerformance(player);
    }

    /// <summary>
    /// 换算本局战斗表现列。只有回放入库的对局才有这些数据，赛前名单与
    /// 旧版本存的记录一律为空。
    /// </summary>
    /// <remarks>
    /// 历史对局的列结构与实时名单不同：实时看的是账号底子，历史看的是
    /// 这一局打了什么。这些字段在实时阶段拿不到，所以单独成一组，
    /// 由界面按是否历史对局决定展示哪一组。
    /// </remarks>
    private void ApplyBattlePerformance(PlayerRecord player)
    {
        SpottingText = player.SpottingDamage.ToString("N0");
        TakenText = player.DamageTaken.ToString("N0");

        // 输出明细按伤害降序，只列出真的打出了伤害的目标。
        var hits = (player.DamageByTarget ?? [])
            .Where(d => d.DamageDealt > 0)
            .ToList();
        DamageTargetCount = hits.Count;
        DamageTargetsText = BuildTargetLines(hits);

        // 承伤明细按对方打出的伤害降序，方向与输出相反。
        var victims = (player.DamageByTarget ?? [])
            .Where(d => d.DamageReceived > 0)
            .OrderByDescending(d => d.DamageReceived)
            .ToList();
        DamageTakenByText = BuildTargetLines(victims, d => d.DamageReceived);

        // 成就按名称排序，同一成就的多次已由映射层写成「名称 ×N」。
        // 显示层再做一次中文转换：早期入库的记录存的是英文名，
        // 不必为了改名把整库回填一遍。
        var achievements = (player.Achievements ?? [])
            .Select(AchievementNames.ToChinese)
            .ToList();
        AchievementCount = achievements.Count;
        AchievementsText = achievements.Count == 0
            ? "无"
            : string.Join(Environment.NewLine, achievements);
        AchievementBadgeText = AchievementCount > 1
            ? $"★{AchievementCount}"
            : "★";

        // 击杀数单独成列，只放红色数字，不再带「击杀」二字：
        // 玩家列已经被昵称和船名占满，加文字必被截断。
        ResultMark = player.Kills > 0
            ? player.Kills.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    /// <summary>本局点亮伤害，历史对局列。0 表示整局没有点亮，与未知区分。</summary>
    public string SpottingText { get; private set; } = "-";

    /// <summary>本局承受伤害，历史对局列。</summary>
    public string TakenText { get; private set; } = "-";

    /// <summary>本局打出伤害的目标艘数，挂在伤害读数后用。</summary>
    public int DamageTargetCount { get; private set; }

    /// <summary>逐船输出明细，悬浮面板用，一行一艘船。</summary>
    public string DamageTargetsText { get; private set; } = "无";

    /// <summary>逐船承伤明细，悬浮面板用，一行一艘船。</summary>
    public string DamageTakenByText { get; private set; } = "无";

    /// <summary>本局获得的成就数量。</summary>
    public int AchievementCount { get; private set; }

    /// <summary>本局获得的成就清单，悬浮面板用。</summary>
    public string AchievementsText { get; private set; } = "无";

    /// <summary>成就徽记文本，带数量。</summary>
    public string AchievementBadgeText { get; private set; } = "★";

    /// <summary>击杀标记文本，无击杀为空串。</summary>
    public string ResultMark { get; private set; } = string.Empty;

    /// <summary>
    /// 输出列前缀的击杀文本，形如 "(4)"，无击杀为空串。
    /// </summary>
    /// <remarks>
    /// 括号只包住击杀数，伤害数字独立着色，所以拆成单独的 Run 给输出列拼。
    /// 用全角括号会与伤害数字挤在一起，这里用半角并带一个尾随空格。
    /// </remarks>
    public string KillText => source is { Kills: > 0 } player
        ? $"({player.Kills}) "
        : string.Empty;

    /// <summary>本局是否有击杀，供击杀列在 0 时显示占位符。</summary>
    public bool HasKills => source?.Kills > 0;

    /// <summary>
    /// 把逐船明细拼成悬浮面板用的多行文本，一行一艘船。
    /// </summary>
    /// <remarks>
    /// 不显示对方玩家名：复盘时关心的是哪艘船吃了伤害，昵称没有信息量，
    /// 还会把悬浮框撑得很宽。目标船信息缺失时退回船 ID，至少能对回放。
    /// </remarks>
    private static string BuildTargetLines(
        IReadOnlyList<DamageByTarget> items,
        Func<DamageByTarget, long>? selector = null)
    {
        if (items.Count == 0)
        {
            return "无";
        }

        var lines = new List<string>(items.Count);
        foreach (var item in items)
        {
            long damage = selector is null ? item.DamageDealt : selector(item);

            // 统一走船目录：有数字船 ID 按 ID 查，没有就按英文名反查，
            // 都查不到保留英文名。名单与明细必须是同一套口径。
            string name = ShipCatalog.Resolve(item.TargetShipId, item.TargetShipName);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = item.TargetPlayerId;
            }

            lines.Add($"X {name} {damage:N0}");
        }

        return string.Join(Environment.NewLine, lines);
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
    /// 组队标识，同一小队成员相同。单排或赛前未知时为空串。
    /// </summary>
    /// <remarks>
    /// 界面不直接用它取色：同一局内不同小队需要保证颜色互不相同，
    /// 由 <see cref="MainViewModel"/> 统一分配下标后经
    /// <see cref="SetDivisionColorIndex"/> 写入。
    /// </remarks>
    public string DivisionId => source?.DivisionId ?? string.Empty;

    /// <summary>
    /// 组队配色下标，-1 表示不是组队成员或配色未知。
    /// </summary>
    public int DivisionColorIndex { get; private set; } = -1;

    /// <summary>
    /// 写入组队配色下标。由名单装配方按整场比赛统一分配后调用。
    /// </summary>
    /// <param name="index">配色池下标，-1 表示不着色。</param>
    public void SetDivisionColorIndex(int index)
    {
        if (DivisionColorIndex == index)
        {
            return;
        }

        DivisionColorIndex = index;
        OnPropertyChanged(nameof(DivisionColorIndex));
    }

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
    /// 船名统一走 <see cref="ShipCatalog.Resolve"/>：数字 ID 优先，英文名兜底。
    /// 存量库里旧记录的 ship_id 是内部索引码，ID 查不到时靠英文名反查补上。
    /// </remarks>
    public string ShipLineText
    {
        get
        {
            string ship = source is null
                ? string.Empty
                : ShipCatalog.Resolve(source.ShipId, source.ShipName);

            if (string.IsNullOrEmpty(ship))
            {
                ship = string.Empty;
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
                return source is not null && source.IsHidden ? HiddenLabel : "-";
            }

            double wr = source.AccountWinrate;
            // 档位判定与队伍聚合共用 TeamAggregator 的阈值。
            return TeamAggregator.ClassifyPlayer(wr, source.AccountBattles) switch
            {
                PlayerTier.Unicum => "海王",
                PlayerTier.Ape => "猴子",
                _ => "普通",
            };
        }
    }

    /// <summary>
    /// 整行底色的资源键。由染色规则直接给出，命中哪条用哪条的底色。
    /// </summary>
    /// <remarks>
    /// 空串表示不染色，行背景保持透明。文字色由 <see cref="WinrateBrushKey"/>
    /// 单独控制，两者同源于胜率口径。
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

        return TeamAggregator.ClassifyPlayer(winrate, battles) switch
        {
            PlayerTier.Unicum => "UnicumBrush",
            PlayerTier.Ape => "ApeBrush",
            _ => "TextPrimaryBrush",
        };
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

    private static string ShipClassLabel(ShipClass shipClass) => shipClass.ToDisplayCode();

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

        // 模板里的 {ShipName} 指船名本身，不带舰种与等级前缀。
        string shipName = ShipText.Length > 0 ? ShipText : ShipLineText;

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
