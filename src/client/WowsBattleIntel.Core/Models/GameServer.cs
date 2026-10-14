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
