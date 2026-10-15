using System.Globalization;
using Newtonsoft.Json.Linq;

namespace WowsBattleIntel.Stats;

/// <summary>
/// WG 公开 API 的响应解析。拆出来便于单测，不依赖网络。
/// </summary>
internal static class WgApiParser
{
    /// <summary>
    /// 解析 account/list 响应，得到昵称到账号 ID 的映射。
    /// </summary>
    public static Dictionary<string, string> ParseAccountIds(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = JObject.Parse(json);

        if (root["status"]?.Value<string>() != "ok")
        {
            return result;
        }

        if (root["data"] is not JArray data)
        {
            return result;
        }

        foreach (var item in data)
        {
            string? nickname = item["nickname"]?.Value<string>();
            string? accountId = item["account_id"]?.ToString();
            if (!string.IsNullOrEmpty(nickname) && !string.IsNullOrEmpty(accountId))
            {
                result[nickname] = accountId;
            }
        }

        return result;
    }

    /// <summary>
    /// 账号统计数据。
    /// </summary>
    public sealed class AccountStats
    {
        public double Battles { get; set; } = -1;
        public double Winrate { get; set; } = -1;
        public double AvgDamage { get; set; } = -1;
        public double BattlesSolo { get; set; } = -1;
        public double WinrateSolo { get; set; } = -1;
        public double BattlesDiv2 { get; set; } = -1;
        public double WinrateDiv2 { get; set; } = -1;
        public double BattlesDiv3 { get; set; } = -1;
        public double WinrateDiv3 { get; set; } = -1;
        public bool IsHidden { get; set; }
    }

    /// <summary>
    /// 解析 account/info 响应，得到账号 ID 到统计的映射。
    /// </summary>
    public static Dictionary<string, AccountStats> ParseAccountStats(string json)
    {
        var result = new Dictionary<string, AccountStats>(StringComparer.Ordinal);
        var root = JObject.Parse(json);

        if (root["status"]?.Value<string>() != "ok" || root["data"] is not JObject data)
        {
            return result;
        }

        foreach (var prop in data.Properties())
        {
            string accountId = prop.Name;
            if (prop.Value is not JObject entry)
            {
                continue;
            }

            var stats = new AccountStats();

            // 隐藏战绩或数据缺失时标记隐藏。
            if (entry["hidden_profile"]?.Value<bool>() == true || entry["statistics"] is null)
            {
                stats.IsHidden = true;
                result[accountId] = stats;
                continue;
            }

            var statistics = entry["statistics"]!;

            ReadPvp(statistics, "pvp", out double battles, out double winrate,
                out double avgDamage, out bool hasPvp);
            if (hasPvp)
            {
                stats.Battles = battles;
                stats.Winrate = winrate;
                stats.AvgDamage = avgDamage;
            }

            ReadPvp(statistics, "pvp_solo", out double bSolo, out double wSolo, out _, out bool hasSolo);
            if (hasSolo)
            {
                stats.BattlesSolo = bSolo;
                stats.WinrateSolo = wSolo;
            }

            ReadPvp(statistics, "pvp_div2", out double bDiv2, out double wDiv2, out _, out bool hasDiv2);
            if (hasDiv2)
            {
                stats.BattlesDiv2 = bDiv2;
                stats.WinrateDiv2 = wDiv2;
            }

            ReadPvp(statistics, "pvp_div3", out double bDiv3, out double wDiv3, out _, out bool hasDiv3);
            if (hasDiv3)
            {
                stats.BattlesDiv3 = bDiv3;
                stats.WinrateDiv3 = wDiv3;
            }

            result[accountId] = stats;
        }

        return result;
    }

    /// <summary>
    /// 从 statistics 节点读取某个模式分组的场次、胜率与场均伤害。
    /// </summary>
    private static void ReadPvp(
        JToken statistics,
        string key,
        out double battles,
        out double winrate,
        out double avgDamage,
        out bool hasData)
    {
        battles = -1;
        winrate = -1;
        avgDamage = -1;
        hasData = false;

        if (statistics[key] is not JObject block || !block.HasValues)
        {
            return;
        }

        double? b = block["battles"]?.Value<double?>();
        double? w = block["wins"]?.Value<double?>();
        if (b is null || w is null)
        {
            return;
        }

        battles = b.Value;
        winrate = b.Value == 0 ? 0 : w.Value / b.Value;

        // 伤害为可选字段，缺省时保持 -1 哨兵。
        double? damage = block["damage_dealt"]?.Value<double?>();
        if (damage.HasValue)
        {
            avgDamage = b.Value == 0 ? 0 : damage.Value / b.Value;
        }

        hasData = true;
    }

    /// <summary>
    /// 解析船只统计响应，得到该玩家该船的场次、胜率、场均伤害。
    /// </summary>
    public static (double Battles, double Winrate, double AvgDamage, double AvgExp) ParseShipStats(
        string json,
        string accountId)
    {
        var root = JObject.Parse(json);
        if (root["status"]?.Value<string>() != "ok"
            || root["data"]?[accountId] is not JArray ships
            || ships.Count == 0)
        {
            return (-1, -1, -1, -1);
        }

        var ship = ships[0];
        var pvp = ship["pvp"];
        if (pvp is null)
        {
            return (-1, -1, -1, -1);
        }

        double battles = pvp["battles"]?.Value<double?>() ?? 0;
        double wins = pvp["wins"]?.Value<double?>() ?? 0;
        double damage = pvp["damage_dealt"]?.Value<double?>() ?? 0;
        double exp = pvp["original_exp"]?.Value<double?>() ?? pvp["exp"]?.Value<double?>() ?? 0;

        if (battles <= 0)
        {
            return (0, 0, 0, 0);
        }

        return (battles, wins / battles, damage / battles, exp / battles);
    }

    /// <summary>
    /// 单船统计条目。
    /// </summary>
    public sealed class ShipStatsEntry
    {
        public double Battles { get; set; }
        public double Wins { get; set; }

        /// <summary>累计伤害，用于计算场均。旧数据无此字段时为 -1。</summary>
        public double DamageDealt { get; set; } = -1;
    }

    /// <summary>
    /// 解析 ships/stats 响应，得到账号 ID 到全部船只统计的映射。
    /// </summary>
    /// <remarks>
    /// 不带 ship_id 过滤时响应为该玩家的全量船只列表，逐条含 pvp 块。
    /// 某些船没有 pvp 数据，即未打过 PvP，跳过。
    /// </remarks>
    public static Dictionary<string, Dictionary<string, ShipStatsEntry>> ParseAllShipStats(
        string json)
    {
        var result = new Dictionary<string, Dictionary<string, ShipStatsEntry>>(StringComparer.Ordinal);
        var root = JObject.Parse(json);

        if (root["status"]?.Value<string>() != "ok" || root["data"] is not JObject data)
        {
            return result;
        }

        foreach (var prop in data.Properties())
        {
            string accountId = prop.Name;
            if (prop.Value is not JArray ships)
            {
                continue;
            }

            var byShip = new Dictionary<string, ShipStatsEntry>(StringComparer.Ordinal);
            foreach (var ship in ships)
            {
                string? shipId = ship["ship_id"]?.ToString();
                if (ship["pvp"] is not JObject pvp || string.IsNullOrEmpty(shipId))
                {
                    continue;
                }

                double? battles = pvp["battles"]?.Value<double?>();
                double? wins = pvp["wins"]?.Value<double?>();
                if (battles is null || wins is null || battles.Value <= 0)
                {
                    continue;
                }

                double? damage = pvp["damage_dealt"]?.Value<double?>();
                byShip[shipId] = new ShipStatsEntry
                {
                    Battles = battles.Value,
                    Wins = wins.Value,
                    DamageDealt = damage ?? -1,
                };
            }

            if (byShip.Count > 0)
            {
                result[accountId] = byShip;
            }
        }

        return result;
    }

    /// <summary>
    /// 解析 encyclopedia/ships 响应 meta 节点，取目录总页数。
    /// </summary>
    public static int ParseShipsPageTotal(string json)
    {
        var root = JObject.Parse(json);
        return root["meta"]?["page_total"]?.Value<int?>() ?? 1;
    }

    /// <summary>
    /// 解析 encyclopedia/ships 响应，得到 ship_id 到舰种字符串的映射。
    /// </summary>
    /// <remarks>
    /// 舰种取 type 字段，形如 "Destroyer"、"AirCarrier"。归一化交给调用方。
    /// </remarks>
    public static Dictionary<string, string> ParseShipClasses(
        string json,
        Dictionary<string, string> into,
        Dictionary<string, int>? tierInto = null)
    {
        var root = JObject.Parse(json);
        if (root["status"]?.Value<string>() != "ok" || root["data"] is not JObject data)
        {
            return into;
        }

        foreach (var prop in data.Properties())
        {
            if (prop.Value is not JObject entry)
            {
                continue;
            }

            string? type = entry["type"]?.Value<string>();
            if (!string.IsNullOrEmpty(type))
            {
                into[prop.Name] = type;
            }

            // 等级目录可选填充，供常用等级计算。
            int? tier = entry["tier"]?.Value<int?>();
            if (tier.HasValue && tierInto is not null)
            {
                tierInto[prop.Name] = tier.Value;
            }
        }

        return into;
    }

    /// <summary>
    /// 从服务器枚举取 API 域名。
    /// </summary>
    public static string GetApiHost(Core.Models.GameServer server) => server switch
    {
        Core.Models.GameServer.Ru => "api.korabli.su",
        Core.Models.GameServer.Eu => "api.worldofwarships.eu",
        Core.Models.GameServer.Na => "api.worldofwarships.com",
        Core.Models.GameServer.Asia => "api.worldofwarships.asia",
        Core.Models.GameServer.Cn => "api.wowsgame.cn",
        _ => throw new ArgumentOutOfRangeException(nameof(server), "无法为 AUTO 解析 API 域名。"),
    };

    /// <summary>
    /// 把账号统计字段格式化为不变文化字符串，便于日志与调试。
    /// </summary>
    public static string Describe(AccountStats stats) =>
        string.Create(CultureInfo.InvariantCulture,
            $"battles={stats.Battles} winrate={stats.Winrate} hidden={stats.IsHidden}");
}
