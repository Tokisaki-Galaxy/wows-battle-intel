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
    /// 把快照的账号层特征并入玩家记录。
    /// </summary>
    /// <remarks>
    /// 只补账号层字段，单局表现与组队信息保持回放原值。
    /// 返回新对象，不改动入参。
    /// </remarks>
    private static PlayerRecord Merge(PlayerRecord player, PlayerSnapshot snapshot)
    {
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
            AccountWinrateSolo = snapshot.AccountWinrateSolo,
            AccountBattlesSolo = snapshot.AccountBattlesSolo,
            AccountWinrateDiv2 = snapshot.AccountWinrateDiv2,
            AccountBattlesDiv2 = snapshot.AccountBattlesDiv2,
            AccountWinrateDiv3 = snapshot.AccountWinrateDiv3,
            AccountBattlesDiv3 = snapshot.AccountBattlesDiv3,
            WeightedWinrate = snapshot.WeightedWinrate,

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
}
