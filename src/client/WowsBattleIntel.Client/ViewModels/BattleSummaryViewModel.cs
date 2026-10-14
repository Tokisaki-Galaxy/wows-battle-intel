using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 对局列表中的一项。
/// </summary>
public sealed class BattleSummaryViewModel
{
    /// <summary>领域模型的对局记录。</summary>
    public required BattleRecord Battle { get; init; }

    /// <summary>来源 JSON 文件路径，来自数据库列表时为空。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>该局的赛前预测胜率，无预测时为空。</summary>
    public double? PredictedWinProbability { get; init; }

    /// <summary>开始时间，UTC。</summary>
    public DateTimeOffset StartedAt => Battle.StartedAt;

    /// <summary>列表主行显示文本。</summary>
    public string DisplayText =>
        $"{StartedAt.ToLocalTime():MM-dd HH:mm}  {Battle.MapId}  "
        + $"{ResultLabel}  {BattleTypeLabel}";

    /// <summary>列表副行显示文本，含预测与参战人数。</summary>
    public string SecondaryText
    {
        get
        {
            string prediction = PredictedWinProbability is { } p
                ? $"预测 {p:P0}"
                : "无预测";
            return $"{prediction} · {DisplayLabels.Server(Battle.Server)} · {DisplayLabels.Duration(Battle.DurationSeconds)}";
        }
    }

    /// <summary>对局类型标签。</summary>
    public string BattleTypeLabel => DisplayLabels.BattleType(Battle.BattleType);

    /// <summary>结果单字。</summary>
    public string ResultLabel => DisplayLabels.Outcome(Battle.Outcome);

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
