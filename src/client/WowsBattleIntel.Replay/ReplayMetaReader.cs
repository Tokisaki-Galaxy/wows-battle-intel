using System.Buffers;
using System.Text;

namespace WowsBattleIntel.Replay;

/// <summary>
/// 从回放文件头部读取元信息，当前只需要 build 号。
/// </summary>
/// <remarks>
/// .wowsreplay 的结构是文件开头一段明文 JSON meta，后跟压缩的包数据。
/// clientVersionFromExe / clientVersionFromXml 形如 "14,10,0,10846255"，
/// 末段即 build 号，落在前几百字节内。整个 meta 的长度写在文件最前的
/// 4 字节 little-endian 整数里，但直接扫前 4 KB 就足够命中版本字段，
/// 不必处理长度语义。
/// </remarks>
public static class ReplayMetaReader
{
    /// <summary>扫描回放头部以定位版本字段的字节数。</summary>
    private const int HeadScanBytes = 4096;

    /// <summary>版本字段值的匹配模式：逗号分隔的数字，末段是 build 号。</summary>
    private static readonly System.Text.RegularExpressions.Regex BuildPattern = new(
        "clientVersionFrom(?:Exe|Xml)\"\\s*:\\s*\"(\\d+),(\\d+),(\\d+),(\\d+)\"",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 读取回放对应的游戏 build 号。
    /// </summary>
    /// <param name="replayPath">回放文件路径。</param>
    /// <returns>build 号，字段缺失或文件异常时为 null。</returns>
    public static uint? ReadBuildNumber(string replayPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayPath);

        try
        {
            using var stream = File.OpenRead(replayPath);
            var head = ArrayPool<byte>.Shared.Rent(HeadScanBytes);
            try
            {
                int read = 0;
                while (read < HeadScanBytes)
                {
                    int n = stream.Read(head, read, HeadScanBytes - read);
                    if (n == 0)
                    {
                        break;
                    }
                    read += n;
                }

                string text = Encoding.UTF8.GetString(head, 0, read);
                var match = BuildPattern.Match(text);
                return match.Success
                    && uint.TryParse(match.Groups[4].Value, out uint build)
                    ? build
                    : null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head);
            }
        }
        catch (IOException)
        {
            // 文件被游戏占用或磁盘异常，解析主流程会给出自己的错误。
            return null;
        }
    }
}
