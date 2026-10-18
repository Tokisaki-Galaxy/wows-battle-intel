namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 一局战斗的完整记录，包含参战者与结果。
/// </summary>
public sealed class BattleRecord
{
    /// <summary>
    /// 对局唯一标识。由开始时间、地图与参战者集合生成，保证同一局稳定可复现。
    /// </summary>
    public string BattleId { get; set; } = string.Empty;

    /// <summary>开始时间，UTC。</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>结束时间，UTC。未解析回放时为空。</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>地图标识。</summary>
    public string MapId { get; set; } = string.Empty;

    /// <summary>对局类型。</summary>
    public BattleType BattleType { get; set; } = BattleType.Unknown;

    /// <summary>游戏版本，用于回放解析匹配。</summary>
    public string GameVersion { get; set; } = string.Empty;

    /// <summary>服务器。</summary>
    public GameServer Server { get; set; } = GameServer.Auto;

    /// <summary>对局结果。</summary>
    public BattleOutcome Outcome { get; set; } = BattleOutcome.Unknown;

    /// <summary>友方得分。</summary>
    public int AllyScore { get; set; }

    /// <summary>敌方得分。</summary>
    public int EnemyScore { get; set; }

    /// <summary>对局时长秒。</summary>
    public int DurationSeconds { get; set; }

    /// <summary>参战者列表。</summary>
    public List<PlayerRecord> Players { get; set; } = [];

    /// <summary>本地回放文件路径，可为空。</summary>
    public string? ReplayPath { get; set; }

    /// <summary>友方玩家。</summary>
    public IEnumerable<PlayerRecord> Allies => Players.Where(p => p.IsAlly);

    /// <summary>敌方玩家。</summary>
    public IEnumerable<PlayerRecord> Enemies => Players.Where(p => !p.IsAlly);

    /// <summary>排除机器人后的参战者。</summary>
    public IEnumerable<PlayerRecord> HumanPlayers => Players.Where(p => !p.IsBot);

    /// <summary>
    /// 敌方是否没有真人：全机器人，或敌方真人均为隐藏战绩。
    /// </summary>
    /// <remarks>
    /// 纯人机局里模型只见一方有人，双方胜率差被推到极端，概率打满到展示
    /// 上限，读数没有意义。预测器识别这类对局后走专用分支，给出不依赖
    /// 模型的保守读数，置信度同步压低。隐藏战绩真人虽是真人，但拿不到
    /// 任何特征，模型对敌方同样一无所知，一并归入此分支。
    /// </remarks>
    public bool EnemyHasNoObservableHumans =>
        Enemies.All(p => p.IsBot)
        || Enemies.Where(p => !p.IsBot).All(p => p.IsHidden);
}
