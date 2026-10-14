using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 领域枚举的中文显示文本。
/// </summary>
/// <remarks>
/// 集中在一处，避免各视图模型各写一份枚举到中文的映射，改口径时只改这里。
/// </remarks>
internal static class DisplayLabels
{
    /// <summary>对局结果单字。</summary>
    public static string Outcome(BattleOutcome outcome) => outcome switch
    {
        BattleOutcome.Win => "胜",
        BattleOutcome.Loss => "负",
        BattleOutcome.Draw => "平",
        _ => "未知",
    };

    /// <summary>对局类型名称。</summary>
    public static string BattleType(BattleType type) => type switch
    {
        Core.Models.BattleType.Random => "随机",
        Core.Models.BattleType.Ranked => "排位",
        Core.Models.BattleType.Coop => "联合作战",
        Core.Models.BattleType.Clan => "公会战",
        Core.Models.BattleType.Operation => "行动",
        Core.Models.BattleType.Training => "训练房",
        _ => "未知",
    };

    /// <summary>服务器名称。</summary>
    public static string Server(GameServer server) => server switch
    {
        GameServer.Ru => "俄服",
        GameServer.Eu => "欧服",
        GameServer.Na => "美服",
        GameServer.Asia => "亚服",
        GameServer.Cn => "国服",
        _ => "未知",
    };

    /// <summary>把秒数格式化为分秒。</summary>
    public static string Duration(int seconds) =>
        seconds <= 0 ? "-" : $"{seconds / 60} 分 {seconds % 60} 秒";
}
