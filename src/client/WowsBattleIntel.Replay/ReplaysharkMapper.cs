using System.Globalization;
using WowsBattleIntel.Core.Identity;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Replay.Dtos;

namespace WowsBattleIntel.Replay;

/// <summary>
/// 把 replayshark 的输出映射成项目领域模型。
/// </summary>
/// <remarks>
/// 映射只做结构转换与语义归一，不做业务判断。友敌判定、组队规模推算
/// 这类需要上下文的信息在这里完成，因为只有这里同时掌握整份输出的全貌。
/// </remarks>
public static class ReplaysharkMapper
{
    /// <summary>
    /// 把 replayshark 输出映射成战斗记录。
    /// </summary>
    /// <param name="output">replayshark 的输出。</param>
    /// <param name="replayPath">回放文件路径，可为空。</param>
    /// <returns>战斗记录。未完成对局返回 null。</returns>
    public static BattleRecord? Map(ReplaysharkOutput output, string? replayPath = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        // 结果字段有两个来源：battle_result 与 results_battle_result。
        // 不同游戏版本填充的字段不同，15.6 常用前者，15.7 起前者可能为 null。
        // 二者取先非空者，都为空才判定为未完成对局。
        var result = output.Metadata.BattleResult ?? output.Metadata.ResultsBattleResult;
        if (result is null)
        {
            return null;
        }

        // 时长在部分版本为 null，不再作为硬性条件。
        // 缺失时记 0，由对局结果的有无决定是否接纳。
        double playedDuration = output.Metadata.PlayedDuration ?? 0;
        if (playedDuration < 0)
        {
            playedDuration = 0;
        }

        // 友敌判定的基准是录制者所在队伍。
        //
        // 关键：不能用 battle_result.team_id。实测确认它是获胜方队伍编号，
        // 而非录制者队伍。失败局的胜方恰好是对手，用它会把失败局的友敌
        // 整体判反，训练数据里一半样本的特征与标签错配。
        //
        // 正确依据是 is_self 标记的玩家，其 team_id 即录制者所在队伍。
        // 回放必然恰有一名录制者，缺失时退回胜方队伍并在下方注释说明局限。
        var recorder = output.Players.FirstOrDefault(p => p.IsSelf);
        int recorderTeamId = recorder?.TeamId ?? result.TeamId;

        var divisionSizes = ComputeDivisionSizes(output.Players);

        var players = new List<PlayerRecord>(output.Players.Count);
        foreach (var rp in output.Players)
        {
            players.Add(MapPlayer(rp, recorderTeamId, divisionSizes));
        }

        var battle = new BattleRecord
        {
            StartedAt = ParseTimestamp(output.Metadata.Timestamp),
            FinishedAt = ParseTimestamp(output.Metadata.Timestamp)
                .AddSeconds(playedDuration),
            MapId = output.Metadata.Map,
            BattleType = MapBattleType(output.Metadata.GameType, output.Metadata.MatchGroup),
            GameVersion = output.Metadata.Version.ToVersionString(),
            Server = MapServer(output.Players.FirstOrDefault(p => p.IsSelf)?.Realm
                ?? output.Players.FirstOrDefault()?.Realm),
            Outcome = MapOutcome(result.Type),
            DurationSeconds = (int)Math.Round(playedDuration),
            Players = players,
            ReplayPath = replayPath,
        };

        battle.BattleId = BattleIdGenerator.Generate(battle);
        return battle;
    }

    /// <summary>
    /// 映射单个玩家。
    /// </summary>
    private static PlayerRecord MapPlayer(
        ReplaysharkPlayer rp,
        int recorderTeamId,
        IReadOnlyDictionary<long, int> divisionSizes)
    {
        var sr = rp.ServerResults;

        var player = new PlayerRecord
        {
            AccountId = rp.DbId.ToString(CultureInfo.InvariantCulture),
            PlayerName = rp.Name,
            Server = MapServer(rp.Realm),
            ClanTag = string.IsNullOrEmpty(rp.Clan) ? string.Empty : $"[{rp.Clan}]",
            // 与录制者同队为友方。
            IsAlly = rp.TeamId == recorderTeamId,
            IsBot = rp.IsBot,
            IsHidden = rp.IsHiddenProfile,
            ShipId = rp.ShipIndex,
            ShipName = rp.ShipName,
            ShipTier = rp.ShipTier,
            ShipClass = MapShipClass(rp.ShipClass),
            DivisionSize = rp.DivisionId is long divId && divisionSizes.TryGetValue(divId, out int size)
                ? size
                : 1,

            // 单局战斗表现，来自服务器权威结果。
            DamageDealt = sr?.Damage ?? 0,
            DamageTaken = sr?.ReceivedDamage ?? 0,
            Kills = sr?.Kills ?? 0,
            SpottingDamage = sr?.SpottingDamage ?? 0,
            PotentialDamage = sr?.PotentialDamage ?? 0,
            Survived = rp.Survived,
            SurvivedSeconds = rp.TimeLivedSecs ?? 0,
            ExpEarned = sr?.Xp ?? 0,
        };

        return player;
    }

    /// <summary>
    /// 从组队 ID 推算每个组队的规模。
    /// </summary>
    private static Dictionary<long, int> ComputeDivisionSizes(IEnumerable<ReplaysharkPlayer> players)
    {
        var counts = new Dictionary<long, int>();
        foreach (var p in players)
        {
            if (p.DivisionId is long id)
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// 解析 ISO 8601 时间戳。
    /// </summary>
    private static DateTimeOffset ParseTimestamp(string timestamp)
    {
        if (DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var result))
        {
            return result;
        }

        return DateTimeOffset.MinValue;
    }

    /// <summary>
    /// 映射对局类型。
    /// </summary>
    /// <remarks>
    /// game_type 是首选依据，取值形如 RandomBattle、CooperativeBattle。
    /// 不同版本与模式的拼写不完全一致，因此同时认几种等价写法，无法识别时
    /// 再退回 match_group 判断。match_group 更粗但更稳定，作为兜底。
    /// </remarks>
    public static BattleType MapBattleType(string gameType, string matchGroup)
    {
        string key = (gameType ?? string.Empty).Trim().ToUpperInvariant();
        return key switch
        {
            "RANDOMBATTLE" => BattleType.Random,
            "RANKEDBATTLE" => BattleType.Ranked,
            // 实际回放写的是 CooperativeBattle，CoopBattle/PVEBattle 是历史拼写。
            "COOPERATIVEBATTLE" or "COOPBATTLE" or "PVEBATTLE" => BattleType.Coop,
            "CLANBATTLE" => BattleType.Clan,
            "TRAININGBATTLE" or "PRIVATEBATTLE" => BattleType.Training,
            _ => BattleTypeExtensions.FromMatchGroup(matchGroup),
        };
    }

    /// <summary>
    /// 映射服务器。
    /// </summary>
    public static GameServer MapServer(string? realm)
    {
        if (string.IsNullOrWhiteSpace(realm))
        {
            return GameServer.Auto;
        }

        return realm.Trim().ToUpperInvariant() switch
        {
            "RU" => GameServer.Ru,
            "EU" => GameServer.Eu,
            "NA" => GameServer.Na,
            "ASIA" => GameServer.Asia,
            "CN" => GameServer.Cn,
            _ => GameServer.Auto,
        };
    }

    /// <summary>
    /// 映射舰种。
    /// </summary>
    public static ShipClass MapShipClass(string? shipClass)
    {
        if (string.IsNullOrWhiteSpace(shipClass))
        {
            return ShipClass.Unknown;
        }

        return shipClass.Trim().ToUpperInvariant() switch
        {
            "DESTROYER" => ShipClass.Destroyer,
            "CRUISER" => ShipClass.Cruiser,
            "BATTLESHIP" => ShipClass.Battleship,
            "AIRCARRIER" or "CARRIER" => ShipClass.Carrier,
            "SUBMARINE" => ShipClass.Submarine,
            _ => ShipClass.Unknown,
        };
    }

    /// <summary>
    /// 映射战斗结果。
    /// </summary>
    public static BattleOutcome MapOutcome(string? resultType)
    {
        if (string.IsNullOrWhiteSpace(resultType))
        {
            return BattleOutcome.Unknown;
        }

        return resultType.Trim().ToUpperInvariant() switch
        {
            "WIN" => BattleOutcome.Win,
            "LOSS" or "LOSE" => BattleOutcome.Loss,
            "DRAW" => BattleOutcome.Draw,
            _ => BattleOutcome.Unknown,
        };
    }
}
