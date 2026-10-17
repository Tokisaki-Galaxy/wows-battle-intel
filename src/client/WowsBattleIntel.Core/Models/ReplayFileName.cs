using System.Globalization;

namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 回放文件名里的开战时间。
/// </summary>
/// <remarks>
/// 游戏把回放命名为 <c>yyyyMMdd_HHmmss_船名_地图名.wowsreplay</c>，时间部分是
/// 本机本地时间，时区信息包含在文件名里但不带偏移。历史回放补录要在解析前
/// 拿到开战时间（列表排序、去重、与库比对都靠它），因此这里提供一个与
/// 回放解析链路无关的读取口径。
///
/// 固定用不变式文化解析，避免不同区域设置下把日与月互换。
/// </remarks>
public static class ReplayFileName
{
    /// <summary>时间戳部分的长度：yyyyMMdd_HHmmss 共 15 个字符。</summary>
    public const int TimestampLength = 15;

    private const string TimestampFormat = "yyyyMMdd_HHmmss";

    /// <summary>
    /// 从回放文件名中取出开战时间。
    /// </summary>
    /// <param name="fileName">文件名或完整路径。</param>
    /// <param name="startedAt">解析出的开战时间，按本地时区解释。</param>
    /// <returns>文件名合规则为 true。</returns>
    public static bool TryParseTimestamp(string? fileName, out DateTimeOffset startedAt)
    {
        return TryParseTimestamp(fileName, TimeZoneInfo.Local, out startedAt);
    }

    /// <summary>
    /// 从回放文件名中取出开战时间，按指定时区补偏移。
    /// </summary>
    /// <param name="fileName">文件名或完整路径。</param>
    /// <param name="timeZone">文件名时间所属的时区。</param>
    /// <param name="startedAt">解析出的开战时间。</param>
    /// <returns>文件名合规则为 true。</returns>
    public static bool TryParseTimestamp(
        string? fileName,
        TimeZoneInfo timeZone,
        out DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        startedAt = default;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string name = Path.GetFileName(fileName);
        if (name.Length <= TimestampLength || name[TimestampLength] != '_')
        {
            // 时间戳之后必须紧跟分隔符，否则只是碰巧以数字开头的名字。
            return false;
        }

        if (!DateTime.TryParseExact(
                name.AsSpan(0, TimestampLength),
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            return false;
        }

        // 文件名只写到秒，用给定时区补上偏移，与解析出来的记录同一口径。
        startedAt = new DateTimeOffset(local, timeZone.GetUtcOffset(local));
        return true;
    }
}
