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
    /// 列表首行显示文本：地图名与对局类型。
    /// </summary>
    public string DisplayText =>
        $"{Battle.MapId}  {BattleTypeLabel}";

    /// <summary>
    /// 列表次行显示文本：时间、服务器与时长。
    /// </summary>
    public string SecondaryText =>
        $"{StartedAt.ToLocalTime():MM-dd HH:mm} · {DisplayLabels.Server(Battle.Server)} · "
        + $"{DisplayLabels.Duration(Battle.DurationSeconds)}";

    /// <summary>预测概率小字，无预测时提示未预测。</summary>
    public string PredictionText => PredictedWinProbability is { } p
        ? $"预测 {p:P0}"
        : "未预测";

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

    /// <summary>是否实时对局。开始时间在近三小时内的对局视为观测会话内的记录。</summary>
    public bool IsLive => StartedAt >= DateTimeOffset.UtcNow.AddHours(-3);

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

