using System.IO;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 游戏服务器自动探测。
/// </summary>
/// <remarks>
/// 逐行复刻 ApeRadar 的 ServerExt.AutoDetectServer：读游戏目录下
/// profile/clientrunner.log，取最后一次 "Selected realm: xx" 的 realm。
/// 这是唯一可靠的服务器来源，回放与赛前名单都不带该信息（15.9 的
/// tempArenaInfo 无 realm 字段）。
///
/// 日志由游戏持续追加，读取必须共享写与删除权限。
/// </remarks>
public static class ServerResolver
{
    /// <summary>日志里 realm 行的前缀。</summary>
    private const string RealmPrefix = "Selected realm: ";

    /// <summary>
    /// 从游戏目录推断服务器。探测失败返回 <see cref="GameServer.Auto"/>。
    /// </summary>
    /// <param name="gamePath">游戏安装目录。</param>
    public static GameServer Resolve(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return GameServer.Auto;
        }

        string logPath = Path.Combine(gamePath, "profile", "clientrunner.log");
        try
        {
            if (!File.Exists(logPath))
            {
                return GameServer.Auto;
            }

            using var stream = new FileStream(
                logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();

            int index = text.LastIndexOf(RealmPrefix, StringComparison.Ordinal);
            if (index < 0)
            {
                return GameServer.Auto;
            }

            index += RealmPrefix.Length;
            int end = text.IndexOf('\n', index);
            string realm = (end < 0 ? text[index..] : text[index..end]).Trim();

            return realm.ToLowerInvariant() switch
            {
                "ru" => GameServer.Ru,
                "eu" => GameServer.Eu,
                "na" => GameServer.Na,
                "asia" => GameServer.Asia,
                "cn" => GameServer.Cn,
                _ => GameServer.Auto,
            };
        }
        catch
        {
            // 日志被占用或权限受限时退回 Auto，调用方另行兜底。
            return GameServer.Auto;
        }
    }
}
