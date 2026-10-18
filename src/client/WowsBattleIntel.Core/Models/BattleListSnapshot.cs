namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 回放目录里的一份回放文件。
/// </summary>
/// <param name="Path">完整路径。</param>
/// <param name="StartedAt">文件名里的开战时间。</param>
/// <param name="Length">文件字节数。</param>
/// <param name="LastWriteUtc">最后写入时间，UTC。</param>
public sealed record ReplayFileInfo(
    string Path,
    DateTimeOffset StartedAt,
    long Length,
    DateTimeOffset LastWriteUtc);

/// <summary>
/// 对局列表项的必要字段，只含列表要展示的内容。
/// </summary>
/// <remarks>
/// 列表不需要完整名单。加载全部参战者会让启动变慢，等级标签单独给出，
/// 避免为了一行等级范围去读整张 battle_players 表。
/// </remarks>
/// <param name="BattleId">对局标识。</param>
/// <param name="StartedAt">开战时间。</param>
/// <param name="MapId">地图标识。</param>
/// <param name="BattleType">对局模式。</param>
/// <param name="Server">服务器。</param>
/// <param name="Outcome">结果，未判定时为 Unknown。</param>
/// <param name="DurationSeconds">时长，秒。</param>
/// <param name="AllyTier">友方最高等级，取不到时为 0。</param>
/// <param name="PredictedWinProbability">预测胜率，无预测时为空。</param>
/// <param name="IsPrebattle">是否为赛前占位记录（标识带 :pre 后缀）。</param>
/// <param name="HasPlayers">库里是否已有参战者明细。</param>
/// <param name="ReplayPath">来源回放文件路径，批量导入等无回放来源的记录为空。</param>
/// <param name="AllyMinTier">友方最低等级，取不到时为 0。与 AllyTier 组成房间等级范围。</param>
public sealed record BattleListEntry(
    string BattleId,
    DateTimeOffset StartedAt,
    string MapId,
    BattleType BattleType,
    GameServer Server,
    BattleOutcome Outcome,
    int DurationSeconds,
    int AllyTier,
    double? PredictedWinProbability,
    bool IsPrebattle,
    bool HasPlayers,
    string ReplayPath = "",
    int AllyMinTier = 0);

/// <summary>
/// 历史列表的一次快照。
/// </summary>
/// <param name="Battles">库里已有的对局，按开战时间倒序。</param>
/// <param name="PendingReplays">
/// 回放目录里存在、库里还没有的对局。列表可以先以占位项展示，
/// 用户选中时再解析入库，避免启动时先把几百份回放跑一遍。
/// </param>
public sealed record BattleListSnapshot(
    IReadOnlyList<BattleListEntry> Battles,
    IReadOnlyList<ReplayFileInfo> PendingReplays);
