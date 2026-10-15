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
    /// <param name="snapshotLookup">账号与服务器到快照的查找函数，可为空。</param>
    /// <returns>补齐后的对局，快照缺失的玩家保留哨兵值。</returns>
    public static BattleRecord Enrich(
        BattleRecord battle,
        Func<string, GameServer, PlayerSnapshot?> snapshotLookup)
    {
        ArgumentNullException.ThrowIfNull(battle);
        ArgumentNullException.ThrowIfNull(snapshotLookup);

        var enriched = new List<PlayerRecord>(battle.Players.Count);
        foreach (var player in battle.Players)
        {
            var snapshot = snapshotLookup(player.AccountId, player.Server);
            enriched.Add(snapshot is null ? player : Merge(player, snapshot));
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
    /// 账号层字段无条件并入；单船层只在快照带 ship_id 且与本局船只一致时并入，
    /// 账号级快照的单船哨兵值不会覆盖玩家已有数据。
    /// 返回新对象，不改动入参。
    /// </remarks>
    private static PlayerRecord Merge(PlayerRecord player, PlayerSnapshot snapshot)
    {
        string classStatsJson = snapshot.ClassStatsJson ?? string.Empty;
        var parsedClass = ParseClassStats(player, classStatsJson);

        // 单船合并的前提是快照带本局船 ID。快照来自旧索引码时代的，
        // ShipId 对不上，单船数据保持玩家原值。
        bool shipMatches = !string.IsNullOrEmpty(snapshot.ShipId)
            && string.Equals(snapshot.ShipId, player.ShipId, StringComparison.Ordinal);

        return new PlayerRecord
        {
            // 身份与对局信息，保持回放原值。
            AccountId = player.AccountId,
            PlayerName = player.PlayerName,
            Server = player.Server,
            ClanTag = player.ClanTag,
            IsAlly = player.IsAlly,
            IsBot = player.IsBot,
            IsHidden = player.IsHidden,
            ShipId = player.ShipId,
            ShipName = player.ShipName,
            ShipTier = player.ShipTier,
            ShipClass = player.ShipClass,
            DivisionSize = player.DivisionSize,

            // 账号层特征，来自快照。
            AccountBattles = snapshot.AccountBattles,
            AccountWinrate = snapshot.AccountWinrate,
            AccountAvgDamage = snapshot.AccountAvgDamage,
            AccountWinrateSolo = snapshot.AccountWinrateSolo,
            AccountBattlesSolo = snapshot.AccountBattlesSolo,
            AccountWinrateDiv2 = snapshot.AccountWinrateDiv2,
            AccountBattlesDiv2 = snapshot.AccountBattlesDiv2,
            AccountWinrateDiv3 = snapshot.AccountWinrateDiv3,
            AccountBattlesDiv3 = snapshot.AccountBattlesDiv3,
            WeightedWinrate = snapshot.WeightedWinrate,
            ClassStatsJson = snapshot.ClassStatsJson ?? string.Empty,

            // 单船统计。快照带匹配的 ship_id 时并入，否则保持玩家原值。
            ShipBattles = shipMatches ? snapshot.ShipBattles : player.ShipBattles,
            ShipWinrate = shipMatches ? snapshot.ShipWinrate : player.ShipWinrate,
            ShipAvgDamage = shipMatches ? snapshot.ShipAvgDamage : player.ShipAvgDamage,
            ShipAvgExp = player.ShipAvgExp,

            // 常用等级来自快照，负值表示快照没有该数据，保持玩家原值。
            FavoriteTier = snapshot.FavoriteTier >= 0 ? snapshot.FavoriteTier : player.FavoriteTier,

            // 分舰种统计，从快照 JSON 按本局舰种解析。
            ClassBattles = parsedClass.Battles,
            ClassWins = parsedClass.Wins,

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
    private static (double Battles, double Wins) ParseClassStats(
        PlayerRecord player,
        string? json)
    {
        if (string.IsNullOrEmpty(json) || player.ShipClass == ShipClass.Unknown)
        {
            return (-1, -1);
        }

        try
        {
            var data = JObject.Parse(json);
            if (data[player.ShipClass.ToStorageString()] is not JObject entry)
            {
                return (-1, -1);
            }

            double? battles = entry["battles"]?.Value<double?>();
            double? wins = entry["wins"]?.Value<double?>();
            return battles.HasValue && wins.HasValue ? (battles.Value, wins.Value) : (-1, -1);
        }
        catch (JsonReaderException)
        {
            return (-1, -1);
        }
    }
}
