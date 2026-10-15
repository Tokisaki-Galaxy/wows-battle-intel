using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Abstractions;

/// <summary>
/// 玩家统计快照的存储。
/// </summary>
/// <remarks>
/// 快照是近期状态特征的原料，也是避免时间泄漏的关键。每次采集玩家统计时
/// 落一条带时间戳的快照，训练时只取对局开始时间之前的最近快照。
/// </remarks>
public interface IPlayerSnapshotStore
{
    /// <summary>
    /// 写入一条玩家统计快照。相同账号在同一时间戳重复写入会被忽略。
    /// </summary>
    Task SaveSnapshotAsync(
        PlayerSnapshot snapshot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量写入快照。
    /// </summary>
    Task SaveSnapshotsAsync(
        IReadOnlyList<PlayerSnapshot> snapshots,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取某玩家在指定时间点之前最近的一条快照。
    /// </summary>
    Task<PlayerSnapshot?> GetLatestSnapshotBeforeAsync(
        string accountId,
        GameServer server,
        DateTimeOffset before,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计已有快照的玩家数与记录数。
    /// </summary>
    Task<(int PlayerCount, int SnapshotCount)> GetSnapshotStatsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取指定时间点之后采过快照的账号键集合。
    /// </summary>
    /// <remarks>
    /// 账号键为 accountId:server。供采集命令做断点续传：当天已采过的玩家跳过。
    /// </remarks>
    Task<IReadOnlySet<string>> GetSnapshotAccountKeysSinceAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 玩家统计快照。
/// </summary>
public sealed class PlayerSnapshot
{
    /// <summary>账号 ID。</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>服务器。</summary>
    public GameServer Server { get; set; } = GameServer.Auto;

    /// <summary>采集时间。</summary>
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>账号总场次。</summary>
    public int AccountBattles { get; set; }

    /// <summary>账号总胜率。</summary>
    public double AccountWinrate { get; set; } = -1;

    /// <summary>账号场均伤害，-1 表示未知，旧快照无此字段。</summary>
    public double AccountAvgDamage { get; set; } = -1;

    /// <summary>单人胜率。</summary>
    public double AccountWinrateSolo { get; set; } = -1;

    /// <summary>单人场次。</summary>
    public int AccountBattlesSolo { get; set; } = -1;

    /// <summary>双排胜率。</summary>
    public double AccountWinrateDiv2 { get; set; } = -1;

    /// <summary>双排场次。</summary>
    public int AccountBattlesDiv2 { get; set; } = -1;

    /// <summary>三排胜率。</summary>
    public double AccountWinrateDiv3 { get; set; } = -1;

    /// <summary>三排场次。</summary>
    public int AccountBattlesDiv3 { get; set; } = -1;

    /// <summary>加权胜率。</summary>
    public double WeightedWinrate { get; set; } = -1;

    /// <summary>
    /// 分舰种聚合统计的 JSON，形如 {"BB":{"battles":1000,"wins":560}}。
    /// 空字符串或 null 表示未知，旧快照无此字段。
    /// </summary>
    public string? ClassStatsJson { get; set; }

    /// <summary>快照对应本局船只的标识，空表示纯账号级快照。</summary>
    public string ShipId { get; set; } = string.Empty;

    /// <summary>本局船只的账号场次，-1 表示未知。</summary>
    public double ShipBattles { get; set; } = -1;

    /// <summary>本局船只的账号胜率，-1 表示未知。</summary>
    public double ShipWinrate { get; set; } = -1;

    /// <summary>本局船只的账号场均伤害，-1 表示未知。</summary>
    public double ShipAvgDamage { get; set; } = -1;

    /// <summary>账号最常玩等级，-1 表示未知。</summary>
    public int FavoriteTier { get; set; } = -1;

    /// <summary>数据来源。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 从玩家记录构造快照。
    /// </summary>
    public static PlayerSnapshot FromPlayer(PlayerRecord player, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(player);

        return new PlayerSnapshot
        {
            AccountId = player.AccountId,
            Server = player.Server,
            CapturedAt = capturedAt,
            AccountBattles = (int)Math.Max(0, player.AccountBattles),
            AccountWinrate = player.AccountWinrate,
            AccountAvgDamage = player.AccountAvgDamage,
            AccountWinrateSolo = player.AccountWinrateSolo,
            AccountBattlesSolo = (int)Math.Max(0, player.AccountBattlesSolo),
            AccountWinrateDiv2 = player.AccountWinrateDiv2,
            AccountBattlesDiv2 = (int)Math.Max(0, player.AccountBattlesDiv2),
            AccountWinrateDiv3 = player.AccountWinrateDiv3,
            AccountBattlesDiv3 = (int)Math.Max(0, player.AccountBattlesDiv3),
            WeightedWinrate = player.WeightedWinrate,
            ClassStatsJson = player.ClassStatsJson,
            ShipId = player.ShipId,
            ShipBattles = player.ShipBattles,
            ShipWinrate = player.ShipWinrate,
            ShipAvgDamage = player.ShipAvgDamage,
            FavoriteTier = player.FavoriteTier,
        };
    }
}
