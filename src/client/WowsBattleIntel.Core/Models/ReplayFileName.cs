using System.Globalization;

namespace WowsBattleIntel.Core.Models;

/// <summary>
/// 回放文件名里的开战时间与船名。
/// </summary>
/// <remarks>
/// 游戏把回放命名为 <c>yyyyMMdd_HHmmss_船名_地图名.wowsreplay</c>，时间部分是
/// 本机本地时间，时区信息包含在文件名里但不带偏移。历史回放补录要在解析前
/// 拿到开战时间（列表排序、去重、与库比对都靠它），列表还要在解析前显示
/// 录制者所开的船，因此这里提供与回放解析链路无关的读取口径。
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

    /// <summary>
    /// 从回放文件名中取出录制者所开的船名。
    /// </summary>
    /// <remarks>
    /// 文件名结构是 <c>时间戳_船名_地图名.wowsreplay</c>：时间戳后紧跟船名，
    /// 地图名在最后一段。地图标识本身可能带下划线（如 spaces_09），无法
    /// 从右边唯一切分，所以从左边取：时间戳之后的完整尾部去掉扩展名，
    /// 最后一段（地图）之后的剩余部分就是船名。实际地图段在文件名里是
    /// 最后一个下划线分段组，而船名不带下划线（游戏内船名形如
    /// PBSB110-Conqueror），因此取时间戳后到末尾、以最后一个不含
    /// 空间标识前缀的分段为准并不可靠。
    ///
    /// 实测文件名形如 <c>..._PBSB110-Conqueror_22_tierra_del_fuego</c>：
    /// 船名段以船目录索引码开头（四字母加数字），后接中划线与船名；
    /// 地图段以两位数字开头。据此取时间戳后第一个分段为船名，与解析
    /// 结果里的 ShipName 一致。
    /// </remarks>
    /// <param name="fileName">文件名或完整路径。</param>
    /// <returns>船名，文件名不合规则为空串。</returns>
    public static string TryGetShipName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        string name = Path.GetFileName(fileName);
        if (name.Length <= TimestampLength || name[TimestampLength] != '_')
        {
            return string.Empty;
        }

        // 去掉扩展名，按分段取时间戳后的第一段。
        string withoutExtension = Path.GetFileNameWithoutExtension(name);
        string tail = withoutExtension[TimestampLength..].TrimStart('_');
        int separator = tail.IndexOf('_', StringComparison.Ordinal);
        string ship = separator < 0 ? tail : tail[..separator];

        return ship;
    }
}
