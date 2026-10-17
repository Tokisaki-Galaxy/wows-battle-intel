namespace WowsBattleIntel.Core.Models;

/// <summary>
/// World of Warships 区域服务器。
/// 与 ApeRadar 的取值保持一致，便于数据互通。
/// </summary>
public enum GameServer
{
    /// <summary>未确定，需要根据游戏日志自动探测。</summary>
    Auto = 0,

    /// <summary>俄服 korabli.su。</summary>
    Ru = 1,

    /// <summary>欧服 worldofwarships.eu。</summary>
    Eu = 2,

    /// <summary>美服 worldofwarships.com。</summary>
    Na = 3,

    /// <summary>亚服 worldofwarships.asia。</summary>
    Asia = 4,

    /// <summary>国服 wowsgame.cn。</summary>
    Cn = 5,
}

/// <summary>
/// 服务器的字符串解析。
/// </summary>
public static class GameServerExtensions
{
    /// <summary>
    /// 把回放与 tempArenaInfo 的 realm 缩写转成服务器枚举。
    /// </summary>
    /// <remarks>
    /// Core 的赛前解析与 Replay 的回放解析共用这一张表。无法识别时返回
    /// <see cref="GameServer.Auto"/>，交由日志探测决定。
    /// </remarks>
    public static GameServer ParseRealm(string? realm)
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
}
