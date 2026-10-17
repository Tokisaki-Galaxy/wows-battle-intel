using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 对局列表中的一项。
/// </summary>
/// <remarks>
/// 三种来源共用一个模板：
/// 库里已有的对局带摘要字段；回放目录里尚未入库的对局只带文件名里的
/// 开战时间，作为占位项显示，用户点开时才解析入库；赛前占位记录标成
/// 赛前态。占位项的可编辑属性由 <see cref="Update(BattleListEntry)"/> 在解析
/// 完成后原地刷新，列表不重建。
/// </remarks>
public sealed class BattleSummaryViewModel : ViewModelBase
{
    /// <summary>当前项对应的对局标识。</summary>
    public string BattleId { get; private set; } = string.Empty;

    /// <summary>占位项对应的回放路径。库中已有的项为空串。</summary>
    public string ReplayPath { get; private set; } = string.Empty;

    private DateTimeOffset startedAt;
    private string mapId = string.Empty;
    private BattleType battleType = BattleType.Unknown;
    private GameServer server = GameServer.Auto;
    private BattleOutcome outcome = BattleOutcome.Unknown;
    private int durationSeconds;
    private int allyTier;
    private double? predictedWinProbability;
    private bool isPlaceholder;
    private bool isPrebattle;

    /// <summary>领域模型的对局记录。列表项按需填充，仅赛前与测试场景直接给出。</summary>
    public BattleRecord? Battle { get; private set; }

    /// <summary>该局的赛前预测胜率，无预测时为空。</summary>
    public double? PredictedWinProbability => predictedWinProbability;

    /// <summary>开始时间。</summary>
    public DateTimeOffset StartedAt => startedAt;

    /// <summary>是否为尚未入库的回放占位项。</summary>
    public bool IsPlaceholder => isPlaceholder;

    /// <summary>是否为赛前占位记录。这类记录没有战果，不能被当成完整对局。</summary>
    public bool IsPrebattle => isPrebattle;

    /// <summary>对局类型。</summary>
    public BattleType BattleType => battleType;

    /// <summary>
    /// 列表首行显示文本：对局类型与地图名。
    /// </summary>
    public string DisplayText =>
        isPlaceholder
            ? "待解析回放"
            : mapId.Length == 0
                ? $"赛前名单 · {BattleTypeLabel}"
                : $"{BattleTypeLabel} {mapId}";

    /// <summary>
    /// 列表次行显示文本：时间、服务器与时长。占位项没有时长，显示点击解析。
    /// </summary>
    public string SecondaryText => isPlaceholder
        ? $"{StartedAt.ToLocalTime():MM-dd HH:mm} · 点击解析"
        : mapId.Length == 0
            ? $"{StartedAt.ToLocalTime():MM-dd HH:mm} · {DisplayLabels.Server(server)} · 赛前名单"
            : $"{StartedAt.ToLocalTime():MM-dd HH:mm} · {DisplayLabels.Server(server)} · "
              + $"{DisplayLabels.Duration(durationSeconds)}";

    /// <summary>
    /// 预测概率小字，无预测时提示未预测。
    /// </summary>
    /// <remarks>
    /// 与详情区同口径走展示夹取：模型外推出的 100% 与 0% 会误导，
    /// 同一局在列表与详情必须显示同一个读数。
    ///
    /// 置信度用括号跟在胜率后面，取概率偏离五五开的程度，与详情区预测
    /// 仪表旁边的数字同一个算法。两者同源，用户在两处看到的是同一个判断。
    /// </remarks>
    public string PredictionText
    {
        get
        {
            if (isPlaceholder)
            {
                return "待解析";
            }

            if (predictedWinProbability is not { } p)
            {
                return "未预测";
            }

            double clamped = DisplayCalibration.ClampForDisplay(p);
            double confidence = DisplayCalibration.ConfidenceFromProbability(p);
            return $"预测 {clamped:P0} ({confidence:P0})";
        }
    }

    /// <summary>对局类型标签。</summary>
    public string BattleTypeLabel => DisplayLabels.BattleType(battleType);

    /// <summary>舰船等级，随地图名一起展示。占位项还没有等级。</summary>
    public string TierText => allyTier > 0 ? $"T{allyTier}" : "T?";

    /// <summary>结果单字。</summary>
    public string ResultLabel => isPlaceholder
        ? "待解析"
        : mapId.Length == 0
            ? "赛前"
            : DisplayLabels.Outcome(outcome);

    /// <summary>结果徽章颜色键，胜绿负红，未知与平局用灰。</summary>
    public string ResultBrushKey => outcome switch
    {
        BattleOutcome.Win => "WinBrush",
        BattleOutcome.Loss => "LossBrush",
        _ => "HiddenBrush",
    };

    /// <summary>
    /// 从库中摘要构造。名单与完整对局记录在选中时才读取。
    /// </summary>
    public static BattleSummaryViewModel FromEntry(BattleListEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new BattleSummaryViewModel
        {
            BattleId = entry.BattleId,
            startedAt = entry.StartedAt,
            mapId = entry.MapId,
            battleType = entry.BattleType,
            server = entry.Server,
            outcome = entry.Outcome,
            durationSeconds = entry.DurationSeconds,
            allyTier = entry.AllyTier,
            predictedWinProbability = entry.PredictedWinProbability,
            isPrebattle = entry.IsPrebattle,
        };
    }

    /// <summary>
    /// 从库中已有的完整记录构造。
    /// </summary>
    /// <param name="battle">对局记录。</param>
    /// <param name="predictedWinProbability">预测胜率，无预测时为空。</param>
    public static BattleSummaryViewModel FromBattle(
        BattleRecord battle,
        double? predictedWinProbability = null)
    {
        ArgumentNullException.ThrowIfNull(battle);

        return new BattleSummaryViewModel
        {
            BattleId = battle.BattleId,
            Battle = battle,
            startedAt = battle.StartedAt,
            mapId = battle.MapId,
            battleType = battle.BattleType,
            server = battle.Server,
            outcome = battle.Outcome,
            durationSeconds = battle.DurationSeconds,
            allyTier = FirstAllyTier(battle),
            predictedWinProbability = predictedWinProbability,
            isPrebattle = battle.BattleId.EndsWith(":pre", StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// 从回放文件构造占位项。只带开战时间，解析留给选中时。
    /// </summary>
    public static BattleSummaryViewModel FromReplay(ReplayFileInfo replay)
    {
        ArgumentNullException.ThrowIfNull(replay);

        return new BattleSummaryViewModel
        {
            ReplayPath = replay.Path,
            startedAt = replay.StartedAt,
            isPlaceholder = true,
        };
    }

    /// <summary>
    /// 占位项解析入库后，用正式记录原地刷新各项展示字段。
    /// </summary>
    /// <remarks>
    /// 只更新属性、不换对象：列表已在界面绑定这个实例，换掉会让选中丢失、
    /// 滚动位置跳动。解析失败的项保持占位态，下次点击再试。
    /// </remarks>
    public void Update(BattleListEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        BattleId = entry.BattleId;
        startedAt = entry.StartedAt;
        mapId = entry.MapId;
        battleType = entry.BattleType;
        server = entry.Server;
        outcome = entry.Outcome;
        durationSeconds = entry.DurationSeconds;
        allyTier = entry.AllyTier;
        predictedWinProbability = entry.PredictedWinProbability;
        isPlaceholder = false;
        isPrebattle = entry.IsPrebattle;
        ReplayPath = string.Empty;
        NotifyAll();
    }

    /// <summary>
    /// 用完整记录原地刷新。
    /// </summary>
    public void Update(BattleRecord battle)
    {
        ArgumentNullException.ThrowIfNull(battle);

        BattleId = battle.BattleId;
        Battle = battle;
        startedAt = battle.StartedAt;
        mapId = battle.MapId;
        battleType = battle.BattleType;
        server = battle.Server;
        outcome = battle.Outcome;
        durationSeconds = battle.DurationSeconds;
        allyTier = FirstAllyTier(battle);
        isPlaceholder = false;
        isPrebattle = battle.BattleId.EndsWith(":pre", StringComparison.Ordinal);
        ReplayPath = string.Empty;
        NotifyAll();
    }

    /// <summary>
    /// 重算后更新预测概率并通知列表刷新。
    /// </summary>
    public void UpdatePrediction(double? winProbability)
    {
        if (predictedWinProbability == winProbability)
        {
            return;
        }

        predictedWinProbability = winProbability;
        OnPropertyChanged(nameof(PredictionText));
    }

    /// <summary>
    /// 赛前名单的占位摘要，放进实时区供用户点击。对局结束后由正式入库的
    /// 回放记录在历史区接管。
    /// </summary>
    public static BattleSummaryViewModel FromPreBattle(BattleRecord battle)
    {
        ArgumentNullException.ThrowIfNull(battle);

        return new BattleSummaryViewModel
        {
            BattleId = battle.BattleId,
            Battle = battle,
            startedAt = battle.StartedAt,
            mapId = battle.MapId,
            battleType = battle.BattleType,
            server = battle.Server,
            outcome = battle.Outcome,
            durationSeconds = battle.DurationSeconds,
        };
    }

    private static int FirstAllyTier(BattleRecord battle) =>
        battle.Players
            .Where(p => p.IsAlly && p.ShipTier > 0)
            .Select(p => (int?)p.ShipTier)
            .FirstOrDefault() ?? 0;

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(BattleId));
        OnPropertyChanged(nameof(IsPlaceholder));
        OnPropertyChanged(nameof(IsPrebattle));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(SecondaryText));
        OnPropertyChanged(nameof(PredictionText));
        OnPropertyChanged(nameof(BattleTypeLabel));
        OnPropertyChanged(nameof(TierText));
        OnPropertyChanged(nameof(ResultLabel));
        OnPropertyChanged(nameof(ResultBrushKey));
    }
}
