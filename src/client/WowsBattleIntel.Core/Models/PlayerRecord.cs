namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 一个玩家在单局中的统计与特征数据。
/// 这是从统计源与本地历史拼装出的模型，供特征工程与预测消费。
/// </summary>
/// <remarks>
/// 约定：所有以 -1 表示的字段代表"未知或不可用"，沿用 ApeRadar 的负数哨兵语义，
/// 不要改成 0，否则会与真实的 0 胜率、0 场次混淆。
/// </remarks>
public sealed class PlayerRecord
{
    /// <summary>账号 ID。跨服唯一性以 <see cref="Server"/> 加本字段为准。</summary>
    public string AccountId { get; set; } = "-1";

    /// <summary>对局时的昵称快照。</summary>
    public string PlayerName { get; set; } = string.Empty;

    /// <summary>所在服务器。</summary>
    public GameServer Server { get; set; } = GameServer.Auto;

    /// <summary>公会标签快照，含方括号，如 [ABC]。</summary>
    public string ClanTag { get; set; } = string.Empty;

    /// <summary>阵营。true 为友方，false 为敌方。</summary>
    public bool IsAlly { get; set; }

    /// <summary>是否机器人。</summary>
    public bool IsBot { get; set; }

    /// <summary>是否隐藏战绩。</summary>
    public bool IsHidden { get; set; }

    /// <summary>船 ID。</summary>
    public string ShipId { get; set; } = "-1";

    /// <summary>船名，来自舰船目录或回放翻译。</summary>
    public string ShipName { get; set; } = string.Empty;

    /// <summary>船等级，1 到 11。</summary>
    public int ShipTier { get; set; }

    /// <summary>舰种。</summary>
    public ShipClass ShipClass { get; set; } = ShipClass.Unknown;

    /// <summary>本局该玩家的组队规模，1 为单人，2 为双排，3 为三排。</summary>
    public int DivisionSize { get; set; } = 1;

    // --- 账号基础统计，-1 表示未知 ---

    /// <summary>账号总场次。</summary>
    public double AccountBattles { get; set; } = -1;

    /// <summary>账号总胜率，0 到 1。</summary>
    public double AccountWinrate { get; set; } = -1;

    /// <summary>单人场次。</summary>
    public double AccountBattlesSolo { get; set; } = -1;

    /// <summary>单人胜率。</summary>
    public double AccountWinrateSolo { get; set; } = -1;

    /// <summary>双排场次。</summary>
    public double AccountBattlesDiv2 { get; set; } = -1;

    /// <summary>双排胜率。</summary>
    public double AccountWinrateDiv2 { get; set; } = -1;

    /// <summary>三排场次。</summary>
    public double AccountBattlesDiv3 { get; set; } = -1;

    /// <summary>三排胜率。</summary>
    public double AccountWinrateDiv3 { get; set; } = -1;

    /// <summary>加权胜率，口径见特征工程文档。</summary>
    public double WeightedWinrate { get; set; } = -1;

    /// <summary>个人评分，可为空。</summary>
    public double PersonalRating { get; set; } = -1;

    // --- 单船统计 ---

    /// <summary>该船场次。</summary>
    public double ShipBattles { get; set; } = -1;

    /// <summary>该船胜率。</summary>
    public double ShipWinrate { get; set; } = -1;

    /// <summary>该船场均伤害。</summary>
    public double ShipAvgDamage { get; set; } = -1;

    /// <summary>该船场均经验。</summary>
    public double ShipAvgExp { get; set; } = -1;

    /// <summary>
    /// 分舰种聚合统计的 JSON，形如 {"BB":{"battles":1000,"wins":560}}。
    /// 空字符串表示未知，旧数据无此字段。
    /// </summary>
    public string ClassStatsJson { get; set; } = string.Empty;

    // --- 分舰种统计，由 ClassStatsJson 按本局舰种解析而来，-1 表示未知 ---

    /// <summary>本局舰种的总场次。</summary>
    public double ClassBattles { get; set; } = -1;

    /// <summary>本局舰种的总胜场。</summary>
    public double ClassWins { get; set; } = -1;

    // --- 近期状态，来自本地累积的逐场记录 ---

    /// <summary>最近 20 场胜率，-1 表示样本不足。</summary>
    public double Recent20Winrate { get; set; } = -1;

    /// <summary>最近 20 场实际场次。</summary>
    public int Recent20Battles { get; set; }

    /// <summary>最近 50 场胜率，-1 表示样本不足。</summary>
    public double Recent50Winrate { get; set; } = -1;

    /// <summary>最近 20 场场均伤害。</summary>
    public double Recent20AvgDamage { get; set; } = -1;

    // --- 会话状态 ---

    /// <summary>本次会话已打场次。</summary>
    public int SessionBattles { get; set; }

    /// <summary>本次会话胜率，-1 表示未知。</summary>
    public double SessionWinrate { get; set; } = -1;

    /// <summary>当前连续失败次数。</summary>
    public int ConsecutiveLosses { get; set; }

    /// <summary>玩家当地时间的小时，0 到 23，-1 表示未知。</summary>
    public int LocalHour { get; set; } = -1;

    // --- 服务器归一化 ---

    /// <summary>服务器归一化后的账号胜率百分位，0 到 1，-1 表示未知。</summary>
    public double WinratePercentile { get; set; } = -1;

    /// <summary>账号胜率在本服的 z 分数。</summary>
    public double WinrateZScore { get; set; } = -1;

    // --- 单局战斗表现，来自回放解析，赛后才有 ---
    // 这些字段是预测的标签来源与校准依据，不参与赛前特征。

    /// <summary>本局造成伤害。</summary>
    public long DamageDealt { get; set; }

    /// <summary>本局承受伤害。</summary>
    public long DamageTaken { get; set; }

    /// <summary>本局击杀数。</summary>
    public long Kills { get; set; }

    /// <summary>本局点亮伤害。</summary>
    public long SpottingDamage { get; set; }

    /// <summary>本局潜在伤害。</summary>
    public long PotentialDamage { get; set; }

    /// <summary>本局是否存活。</summary>
    public bool Survived { get; set; }

    /// <summary>本局存活秒数。</summary>
    public int SurvivedSeconds { get; set; }

    /// <summary>本局获得经验。</summary>
    public long ExpEarned { get; set; }

    /// <summary>数据是否可用，隐藏战绩或查询失败时为 false。</summary>
    public bool HasStats => !IsHidden && AccountWinrate >= 0;
}
