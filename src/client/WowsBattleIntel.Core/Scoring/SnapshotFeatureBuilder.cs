using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Scoring;

/// <summary>
/// 用玩家统计快照补齐对局特征。
/// </summary>
/// <remarks>
/// 回放只提供对局名单与战后表现，不含账号历史统计。预测需要账号胜率
/// 这类赛前特征，从玩家统计快照补齐。
///
/// 关于时间泄漏：账号总胜率是长期累积量，一个五千场的老号，几个月内
/// 几百场对局几乎不改变它的总胜率。用"当前"账号胜率近似"对局当时"的
/// 胜率，误差可忽略，这是 XVM 与 ApeRadar 都采用的做法。
///
/// 需要严格防泄漏的近期状态与会话状态由 RecentFormCalculator 处理，
/// 它按时间上界只用对局之前的历史。
/// </remarks>
public static class SnapshotFeatureBuilder
{
    /// <summary>
    /// 用快照补齐对局中每个玩家的账号特征。
    /// </summary>
    /// <param name="battle">对局记录。</param>
    /// <param name="snapshotLookup">账号、服务器与船只到快照的查找函数，可为空。</param>
    /// <returns>补齐后的对局，快照缺失的玩家保留哨兵值。</returns>
    /// <remarks>
    /// 每个玩家查两次：shipId 传空串取账号级最新快照，传本局船 ID 取
    /// 该船的最新快照。两层独立命中，同账号换船不再让单船数据 miss。
    /// </remarks>
    public static BattleRecord Enrich(
        BattleRecord battle,
        Func<string, GameServer, string, PlayerSnapshot?> snapshotLookup)
    {
        ArgumentNullException.ThrowIfNull(battle);
        ArgumentNullException.ThrowIfNull(snapshotLookup);

        var enriched = new List<PlayerRecord>(battle.Players.Count);
        foreach (var player in battle.Players)
        {
            // 查询服务器：玩家记录自己解析出具体服时以它为准，否则用对局级的。
            // 赛前名单的玩家记录是 Auto，对局级已从最近一局推断出具体服，
            // 若只按玩家的 Auto 查，快照表里按真实服落库的行永远查不到。
            GameServer server = player.SnapshotServer == GameServer.Auto ? battle.Server : player.SnapshotServer;
            var accountSnapshot = snapshotLookup(player.AccountId, server, string.Empty);
            var shipSnapshot = string.IsNullOrEmpty(player.ShipId)
                ? null
                : snapshotLookup(player.AccountId, server, player.ShipId);

            // 账号层缺失时退回船级快照，它的行里同样带账号层字段。
            var effective = accountSnapshot ?? shipSnapshot;
            enriched.Add(effective is null ? player : Merge(player, effective, shipSnapshot, server));
        }

        return new BattleRecord
        {
            BattleId = battle.BattleId,
            StartedAt = battle.StartedAt,
            FinishedAt = battle.FinishedAt,
            MapId = battle.MapId,
            BattleType = battle.BattleType,
            GameVersion = battle.GameVersion,
            Server = battle.Server,
            Outcome = battle.Outcome,
            AllyScore = battle.AllyScore,
            EnemyScore = battle.EnemyScore,
            DurationSeconds = battle.DurationSeconds,
            Players = enriched,
            ReplayPath = battle.ReplayPath,
        };
    }

    /// <summary>
    /// 把快照的账号层与单船层特征并入玩家记录。
    /// </summary>
    /// <remarks>
    /// 账号层字段无条件并入；单船层按账号加船维度独立命中，与账号级快照
    /// 是否为最新一条无关。返回新对象，不改动入参。
    ///
    /// 单船层只补空值：玩家记录里已有的单船数据来自本局这条船的快照或在线
    /// 补拉，比“该账号这艘船最近一次被记录时的数据”更新，用旧值覆盖会让
    /// 单船列的数字倒退。
    /// </remarks>
    private static PlayerRecord Merge(
        PlayerRecord player,
        PlayerSnapshot accountSnapshot,
        PlayerSnapshot? shipSnapshot,
        GameServer statsServer)
    {
        string classStatsJson = accountSnapshot.ClassStatsJson ?? string.Empty;
        var parsedClass = ParseClassStats(player, classStatsJson);

        // 快照里的军团标签。旧快照无此字段时为空串，回填不生效。
        string snapshotClanTag = accountSnapshot.ClanTag ?? string.Empty;

        // 单船层来自按船命中的快照，该快照的 ship_id 与本局船必然一致。
        bool hasShipStats = shipSnapshot is not null
            && !string.IsNullOrEmpty(shipSnapshot.ShipId)
            && string.Equals(shipSnapshot.ShipId, player.ShipId, StringComparison.Ordinal);

        // 账号层取更新的一条：账号级快照与船级快照各自独立落库，最新一条
        // 可能落在任一侧。只认账号级会用到比船级更旧的一行。
        PlayerSnapshot account = shipSnapshot is not null
            && hasShipStats
            && shipSnapshot.CapturedAt > accountSnapshot.CapturedAt
            && shipSnapshot.AccountWinrate >= 0
                ? shipSnapshot
                : accountSnapshot;

        return new PlayerRecord
        {
            // 身份与对局信息，保持回放原值。
            AccountId = player.AccountId,
            PlayerName = player.PlayerName,
            Server = player.Server,
            // 快照是按哪个服务器查到的。快照表按 server 列分区，赛前记录的
            // Server 恒为 Auto，不随快照回填就永远查不到自己刚拉下来的数据。
            StatsServer = statsServer,
            // 军团标签：回放自带时保留，缺失时用快照里的补上。
            ClanTag = string.IsNullOrEmpty(player.ClanTag) ? snapshotClanTag : player.ClanTag,
            IsAlly = player.IsAlly,
            IsBot = player.IsBot,
            IsHidden = player.IsHidden,
            ShipId = player.ShipId,
            ShipName = player.ShipName,
            ShipTier = player.ShipTier,
            ShipClass = player.ShipClass,
            DivisionSize = player.DivisionSize,

            // 账号层特征，来自快照。
            AccountBattles = account.AccountBattles,
            AccountWinrate = account.AccountWinrate,
            AccountAvgDamage = account.AccountAvgDamage,
            AccountWinrateSolo = account.AccountWinrateSolo,
            AccountBattlesSolo = account.AccountBattlesSolo,
            AccountWinrateDiv2 = account.AccountWinrateDiv2,
            AccountBattlesDiv2 = account.AccountBattlesDiv2,
            AccountWinrateDiv3 = account.AccountWinrateDiv3,
            AccountBattlesDiv3 = account.AccountBattlesDiv3,
            WeightedWinrate = account.WeightedWinrate,
            ClassStatsJson = account.ClassStatsJson ?? string.Empty,

            // 单船统计。按船命中的快照存在时并入，否则保持玩家原值。
            // 玩家侧为负说明本局这条船从没拿到过数据，才让快照补。
            ShipBattles = player.ShipBattles >= 0 ? player.ShipBattles : shipSnapshot?.ShipBattles ?? player.ShipBattles,
            ShipWinrate = player.ShipWinrate >= 0 ? player.ShipWinrate : shipSnapshot?.ShipWinrate ?? player.ShipWinrate,
            ShipAvgDamage = player.ShipAvgDamage >= 0 ? player.ShipAvgDamage : shipSnapshot?.ShipAvgDamage ?? player.ShipAvgDamage,
            ShipAvgExp = player.ShipAvgExp,

            // 常用等级来自快照，负值表示快照没有该数据，保持玩家原值。
            FavoriteTier = account.FavoriteTier >= 0
                ? account.FavoriteTier
                : player.FavoriteTier,

            // 分舰种统计，从快照 JSON 按本局舰种解析。
            ClassBattles = parsedClass.Battles,
            ClassWins = parsedClass.Wins,
            ClassDamage = parsedClass.Damage,

            // 单局表现，保持回放原值。
            DamageDealt = player.DamageDealt,
            DamageTaken = player.DamageTaken,
            Kills = player.Kills,
            SpottingDamage = player.SpottingDamage,
            PotentialDamage = player.PotentialDamage,
            Survived = player.Survived,
            SurvivedSeconds = player.SurvivedSeconds,
            ExpEarned = player.ExpEarned,
        };
    }

    /// <summary>
    /// 从快照的分舰种 JSON 解析本局舰种的场次与胜场。
    /// </summary>
    /// <remarks>
    /// JSON 缺失、损坏或无本局舰种条目时返回 -1，与负数哨兵语义一致。
    /// </remarks>
    private static (double Battles, double Wins, double Damage) ParseClassStats(
        PlayerRecord player,
        string? json)
    {
        if (string.IsNullOrEmpty(json) || player.ShipClass == ShipClass.Unknown)
        {
            return (-1, -1, -1);
        }

        try
        {
            var data = JObject.Parse(json);
            // 聚合端写入用稳定键（BB/CA/DD/CV/SS），解析端必须用同一套键。
            // ShipClass.ToStorageString 返回枚举名（Cruiser），与聚合键不一致。
            string key = player.ShipClass.ToStorageKey();
            if (key.Length == 0 || data[key] is not JObject entry)
            {
                return (-1, -1, -1);
            }

            double? battles = entry["battles"]?.Value<double?>();
            double? wins = entry["wins"]?.Value<double?>();
            // damage 维度是后加的，旧快照无此字段时为 null，返回 -1 哨兵。
            double? damage = entry["damage"]?.Value<double?>();
            return battles.HasValue && wins.HasValue
                ? (battles.Value, wins.Value, damage ?? -1)
                : (-1, -1, -1);
        }
        catch (JsonReaderException)
        {
            return (-1, -1, -1);
        }
    }
}
