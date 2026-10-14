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

        // 未完成对局：无结果或时长为零，跳过以免污染训练数据。
        if (output.Metadata.BattleResult is null
            || output.Metadata.PlayedDuration is not double playedDuration
            || playedDuration <= 0)
        {
            return null;
        }

        int recorderTeamId = output.Metadata.BattleResult.TeamId;
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
            Outcome = MapOutcome(output.Metadata.BattleResult.Type),
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
            ShipTier = rp.ShipTier,
            ShipClass = MapShipClass(rp.ShipClass),
            DivisionSize = rp.DivisionId is long divId && divisionSizes.TryGetValue(divId, out int size)
                ? size
                : 1,
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
    public static BattleType MapBattleType(string gameType, string matchGroup)
    {
        string key = (gameType ?? string.Empty).Trim().ToUpperInvariant();
        return key switch
        {
            "RANDOMBATTLE" => BattleType.Random,
            "RANKEDBATTLE" => BattleType.Ranked,
            "COOPBATTLE" or "PVEBATTLE" => BattleType.Coop,
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
