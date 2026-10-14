using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 对局列表中的一项。
/// </summary>
public sealed class BattleSummaryViewModel
{
    /// <summary>领域模型的对局记录。</summary>
    public required BattleRecord Battle { get; init; }

    /// <summary>来源 JSON 文件路径。</summary>
    public required string SourcePath { get; init; }

    /// <summary>开始时间，UTC。</summary>
    public DateTimeOffset StartedAt => Battle.StartedAt;

    /// <summary>列表显示文本。</summary>
    public string DisplayText =>
        $"{StartedAt.ToLocalTime():MM-dd HH:mm}  {Battle.MapId}  "
        + $"{ResultLabel}  {BattleTypeLabel}";

    /// <summary>对局类型标签。</summary>
    public string BattleTypeLabel => Battle.BattleType switch
    {
        BattleType.Random => "随机",
        BattleType.Ranked => "排位",
        BattleType.Coop => "联合作战",
        BattleType.Clan => "公会战",
        BattleType.Operation => "行动",
        BattleType.Training => "训练房",
        _ => "未知",
    };

    /// <summary>结果单字。</summary>
    public string ResultLabel => Battle.Outcome switch
    {
        BattleOutcome.Win => "胜",
        BattleOutcome.Loss => "负",
        BattleOutcome.Draw => "平",
        _ => "?",
    };

    /// <summary>
    /// 从领域模型构造。
    /// </summary>
    public static BattleSummaryViewModel FromBattle(BattleRecord battle, string sourcePath)
    {
        return new BattleSummaryViewModel
        {
            Battle = battle,
            SourcePath = sourcePath,
        };
    }
}
