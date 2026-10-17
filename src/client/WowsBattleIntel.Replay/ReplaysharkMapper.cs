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

        // damage_interactions 的键只有目标玩家的 db_id，船名、等级、舰种都在
        // 同一次解析的 players 数组里。先建一次字典供所有玩家共用。
        var playersById = new Dictionary<long, ReplaysharkPlayer>(output.Players.Count);
        foreach (var rp in output.Players)
        {
            playersById[rp.DbId] = rp;
        }

        var players = new List<PlayerRecord>(output.Players.Count);
        foreach (var rp in output.Players)
        {
            players.Add(MapPlayer(rp, recorderTeamId, divisionSizes, playersById));
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
        IReadOnlyDictionary<long, int> divisionSizes,
        IReadOnlyDictionary<long, ReplaysharkPlayer> playersById)
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
            // ship_id 是数字 ID，如 4286581900，WG API 的 ships/stats 端点只认它。
            // 早年这里误存了 ship_index（PJSD012 之类的内部索引码），
            // 导致单船统计查询永远落空，这里必须保持数字 ID。
            ShipId = rp.ShipId.ToString(CultureInfo.InvariantCulture),
            ShipName = rp.ShipName,
            ShipTier = rp.ShipTier,
            ShipClass = MapShipClass(rp.ShipClass),
            DivisionSize = rp.DivisionId is long divId && divisionSizes.TryGetValue(divId, out int size)
                ? size
                : 1,

            // 人数只够区分单排与双排三排，同一局里两个双人队会撞成同一个颜色。
            // 保留原始 division_id 才能让界面按小队着色。
            DivisionId = rp.DivisionId?.ToString(CultureInfo.InvariantCulture),

            // 单局战斗表现，来自服务器权威结果。
            DamageDealt = sr?.Damage ?? 0,
            DamageTaken = sr?.ReceivedDamage ?? 0,
            Kills = sr?.Kills ?? 0,
            SpottingDamage = sr?.SpottingDamage ?? 0,
            PotentialDamage = sr?.PotentialDamage ?? 0,
            Survived = rp.Survived,
            SurvivedSeconds = rp.TimeLivedSecs ?? 0,
            ExpEarned = sr?.Xp ?? 0,

            DamageByTarget = MapDamageByTarget(rp, playersById),
            Achievements = MapAchievements(rp),
        };

        return player;
    }

    /// <summary>
    /// 把某个玩家的 damage_interactions 展开成按目标船组织的明细。
    /// </summary>
    /// <remarks>
    /// 键是目标玩家的 db_id，船信息要从同一局名单反查。名单里没有的目标
    /// 仍然保留，只是船名空着、等级与舰种为未知，伤害数字本身是可信的。
    /// 双方互无伤害的条目没有信息量，直接丢弃，否则每人都会带上整张名单。
    /// </remarks>
    private static IReadOnlyList<DamageByTarget> MapDamageByTarget(
        ReplaysharkPlayer rp,
        IReadOnlyDictionary<long, ReplaysharkPlayer> playersById)
    {
        var interactions = rp.ServerResults?.DamageInteractions;
        if (interactions is null || interactions.Count == 0)
        {
            return Array.Empty<DamageByTarget>();
        }

        var result = new List<DamageByTarget>(interactions.Count);
        foreach (var (targetId, interaction) in interactions)
        {
            if (interaction is null || (interaction.DamageDealt == 0 && interaction.DamageReceived == 0))
            {
                continue;
            }

            ReplaysharkPlayer? target = long.TryParse(
                targetId,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long parsedId)
                && playersById.TryGetValue(parsedId, out var found)
                    ? found
                    : null;

            result.Add(new DamageByTarget
            {
                // 交互字典的键是对方玩家的 db_id，不是船 ID，命名要如实反映。
                TargetPlayerId = targetId,
                // 真正的数字船 ID 从同一局名单里反查，查不到为空串。
                TargetShipId = target?.ShipId.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                TargetShipName = target?.ShipName ?? string.Empty,
                TargetShipTier = target?.ShipTier ?? 0,
                TargetShipClass = target is null ? ShipClass.Unknown : MapShipClass(target.ShipClass),
                DamageDealt = interaction.DamageDealt,
                DamageReceived = interaction.DamageReceived,
            });
        }

        // 伤害高的在前。同伤害再按船名排，保证两次解析的顺序完全一致，
        // 否则比对结果时会因为顺序不同产生假差异。
        result.Sort((a, b) =>
        {
            int byDamage = b.DamageDealt.CompareTo(a.DamageDealt);
            return byDamage != 0
                ? byDamage
                : string.CompareOrdinal(a.TargetShipName, b.TargetShipName);
        });

        return result;
    }

    /// <summary>
    /// 把成就整理成展示文本。同一成就计得多次时写成「名称 ×N」。
    /// </summary>
    /// <remarks>
    /// 优先用 display_name，缺失时退回 name。只保留展示文本而不保留图标键，
    /// 图标资源随游戏版本变化，存下来也未必还能对上。
    /// </remarks>
    private static IReadOnlyList<string> MapAchievements(ReplaysharkPlayer rp)
    {
        if (rp.Achievements is null || rp.Achievements.Count == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(rp.Achievements.Count);
        foreach (var achievement in rp.Achievements)
        {
            if (achievement is null)
            {
                continue;
            }

            string label = string.IsNullOrWhiteSpace(achievement.DisplayName)
                ? achievement.Name
                : achievement.DisplayName;
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            // 回放给的是英文名，界面其余文字都是中文，这里统一换成中文口径。
            label = AchievementNames.ToChinese(label);

            result.Add(achievement.Count > 1
                ? $"{label} ×{achievement.Count}"
                : label);
        }

        return result;
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
    /// <summary>
    /// 映射对局类型。实现收口在 <see cref="BattleTypeExtensions.FromGameType"/>。
    /// </summary>
    public static BattleType MapBattleType(string gameType, string matchGroup) =>
        BattleTypeExtensions.FromGameType(gameType, matchGroup);

    /// <summary>
    /// 映射服务器。实现收口在 <see cref="GameServerExtensions.ParseRealm"/>，
    /// 这里保留薄封装以维持既有调用契约。
    /// </summary>
    public static GameServer MapServer(string? realm) => GameServerExtensions.ParseRealm(realm);

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
