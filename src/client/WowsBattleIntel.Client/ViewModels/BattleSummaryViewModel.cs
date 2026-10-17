using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 对局列表中的一项。
/// </summary>
public sealed class BattleSummaryViewModel : ViewModelBase
{
    /// <summary>领域模型的对局记录。</summary>
    public required BattleRecord Battle { get; init; }

    private double? predictedWinProbability;

    /// <summary>来源 JSON 文件路径，来自数据库列表时为空。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>该局的赛前预测胜率，无预测时为空。</summary>
    public double? PredictedWinProbability
    {
        get => predictedWinProbability;
        init => predictedWinProbability = value;
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

    /// <summary>开始时间，UTC。</summary>
    public DateTimeOffset StartedAt => Battle.StartedAt;

    /// <summary>
    /// 列表首行显示文本：对局类型与地图名。
    /// </summary>
    public string DisplayText =>
        Battle.MapId.Length == 0
            ? $"赛前名单 · {BattleTypeLabel}"
            : $"{BattleTypeLabel} {Battle.MapId}";

    /// <summary>
    /// 列表次行显示文本：时间、服务器与时长。
    /// </summary>
    public string SecondaryText =>
        $"{StartedAt.ToLocalTime():MM-dd HH:mm} · {DisplayLabels.Server(Battle.Server)} · "
        + $"{DisplayLabels.Duration(Battle.DurationSeconds)}";

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
            if (PredictedWinProbability is not { } p)
            {
                return "未预测";
            }

            double clamped = DisplayCalibration.ClampForDisplay(p);
            double confidence = DisplayCalibration.ConfidenceFromProbability(p);
            return $"预测 {clamped:P0} ({confidence:P0})";
        }
    }

    /// <summary>对局类型标签。</summary>
    public string BattleTypeLabel => DisplayLabels.BattleType(Battle.BattleType);

    /// <summary>舰船等级，随地图名一起展示。</summary>
    public string TierText
    {
        get
        {
            // 取己方参战者的常见等级，隐藏战绩或未解析时给占位。
            // 注意不能对 int? 直接 FirstOrDefault：空序列返回 0 会显示 T0。
            int? tier = Battle.Players
                .Where(p => p.IsAlly && p.ShipTier > 0)
                .Select(p => (int?)p.ShipTier)
                .Cast<int?>()
                .DefaultIfEmpty(null)
                .First();

            return tier is { } t ? $"T{t}" : "T?";
        }
    }

    /// <summary>结果单字。</summary>
    public string ResultLabel => DisplayLabels.Outcome(Battle.Outcome);

    /// <summary>结果徽章颜色键，胜绿负红，未知与平局用灰。</summary>
    public string ResultBrushKey => Battle.Outcome switch
    {
        BattleOutcome.Win => "WinBrush",
        BattleOutcome.Loss => "LossBrush",
        _ => "HiddenBrush",
    };

    /// <summary>
    /// 赛前名单的占位摘要，放进实时区供用户点击。不落库，对局结束后
    /// 由正式入库的回放记录在历史区接管。
    /// </summary>
    /// <remarks>
    /// 赛前记录的 MapId 为空，标题显示"赛前名单 · 模式"。
    /// </remarks>
    public static BattleSummaryViewModel FromPreBattle(BattleRecord battle)
    {
        ArgumentNullException.ThrowIfNull(battle);

        return new BattleSummaryViewModel
        {
            Battle = battle,
            SourcePath = "pre-battle",
        };
    }

    /// <summary>
    /// 从领域模型构造。
    /// </summary>
    public static BattleSummaryViewModel FromBattle(
        BattleRecord battle,
        string sourcePath = "",
        double? predictedWinProbability = null)
    {
        ArgumentNullException.ThrowIfNull(battle);

        return new BattleSummaryViewModel
        {
            Battle = battle,
            SourcePath = sourcePath,
            PredictedWinProbability = predictedWinProbability,
        };
    }
}

