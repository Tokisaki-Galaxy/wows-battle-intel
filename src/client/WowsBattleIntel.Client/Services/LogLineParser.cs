using System.Globalization;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 日志行解析。
/// </summary>
/// <remarks>
/// 与 <see cref="LogFileWriter"/> 的写出格式严格对应：
/// yyyy-MM-dd HH:mm:ss.fff [LEVEL] message。
/// 解析失败的行走格，用于容忍手工编辑或旧版本写入的内容。
/// </remarks>
internal static class LogLineParser
{
    /// <summary>时间戳段长度。</summary>
    private const int TimestampLength = 23;

    /// <summary>解析一行日志。</summary>
    public static bool TryParse(string line, out LogEntry entry)
    {
        entry = default!;

        if (line.Length < TimestampLength + 4
            || !DateTime.TryParseExact(
                line.AsSpan(0, TimestampLength),
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp)
            || line[TimestampLength] != ' ')
        {
            return false;
        }

        int close = line.IndexOf(']', TimestampLength + 1);
        if (line[TimestampLength + 1] != '[' || close < TimestampLength + 2)
        {
            return false;
        }

        string levelText = line[(TimestampLength + 2)..close];
        var level = levelText switch
        {
            "WARN" => LogLevel.Warn,
            "ERROR" => LogLevel.Error,
            "INFO" => LogLevel.Info,
            _ => (LogLevel?)null,
        };

        if (level is null)
        {
            return false;
        }

        // 级别段之后紧跟一个空格，其后是消息正文。
        int messageStart = Math.Min(close + 2, line.Length);
        string message = line[messageStart..];
        var local = new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Local));
        entry = new LogEntry(local, level.Value, message);
        return true;
    }
}
