using System.Security.Cryptography;
using System.Text;

namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 同局收敛判定。
/// </summary>
/// <remarks>
/// 同一局战斗会被场上每个玩家的回放各解析一次，产生多份不同的 BattleId。
/// 仅靠 battle_id 去重挡不住这类重复。判定口径：参战玩家 account_id 集合
/// 一致、地图一致、开始时间落在窗口内，即视为同一局。
/// </remarks>
public static class BattleFingerprint
{
    /// <summary>同局时间窗。不同玩家的回放开始时间可能相差几分钟，760 秒覆盖典型场景。</summary>
    public const int EquivalentWindowSeconds = 760;

    /// <summary>
    /// 计算参战玩家签名：排序后的 account_id 拼接后取 SHA256 前 16 个十六进制字符。
    /// </summary>
    /// <remarks>
    /// 排序保证集合等价（与录入顺序无关）。哈希截断到 64 位，24 名参战者的
    /// 组合空间下碰撞概率可忽略。
    /// </remarks>
    public static string PlayerSignature(IEnumerable<PlayerRecord> players)
    {
        ArgumentNullException.ThrowIfNull(players);

        string joined = string.Join(';',
            players.Select(p => p.AccountId).Where(id => !string.IsNullOrWhiteSpace(id))
                .OrderBy(id => id, StringComparer.Ordinal));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// 判断两局是否为同一局的两次录制。
    /// </summary>
    public static bool IsEquivalentTo(BattleRecord left, BattleRecord right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (!string.Equals(left.MapId, right.MapId, StringComparison.Ordinal))
        {
            return false;
        }

        double gapSeconds = Math.Abs((left.StartedAt - right.StartedAt).TotalSeconds);
        if (gapSeconds >= EquivalentWindowSeconds)
        {
            return false;
        }

        return PlayerSignature(left.Players) == PlayerSignature(right.Players);
    }
}
