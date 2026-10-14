using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Identity;

/// <summary>
/// 生成稳定的对局标识。
/// </summary>
/// <remarks>
/// 对局标识必须满足两个性质：同一局在不同客户端上算出同一个值，
/// 且不依赖解析顺序。因此用开始时间、地图、对局类型与排序后的参战者
/// 集合做哈希。不包含随机成分，保证可复现。
/// </remarks>
public static class BattleIdGenerator
{
    /// <summary>
    /// 生成对局标识。
    /// </summary>
    /// <param name="startedAt">开始时间。</param>
    /// <param name="mapId">地图标识。</param>
    /// <param name="battleType">对局类型。</param>
    /// <param name="players">参战者。只需账号 ID 与服务器。</param>
    /// <returns>十六进制对局标识。</returns>
    public static string Generate(
        DateTimeOffset startedAt,
        string mapId,
        BattleType battleType,
        IEnumerable<PlayerRecord> players)
    {
        ArgumentNullException.ThrowIfNull(players);

        // 参战者按账号 ID 加服务器排序，消除顺序差异。
        var participantKeys = players
            .Where(p => !string.IsNullOrEmpty(p.AccountId) && p.AccountId != "-1")
            .Select(p => $"{p.Server}:{p.AccountId}")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        // 截断到秒，避免毫秒差异导致同一局算出不同 ID。
        builder.Append(startedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        builder.Append('|');
        builder.Append(mapId);
        builder.Append('|');
        builder.Append(battleType.ToStorageString());
        builder.Append('|');
        builder.Append(string.Join(',', participantKeys));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 生成对局标识。
    /// </summary>
    public static string Generate(BattleRecord battle)
    {
        ArgumentNullException.ThrowIfNull(battle);

        return Generate(battle.StartedAt, battle.MapId, battle.BattleType, battle.Players);
    }
}
