using System.IO;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 回放目录扫描。
/// </summary>
/// <remarks>
/// 观测器与历史补录共用这一份扫描逻辑，保证两边“什么算一份可用的回放”
/// 口径一致：递归扫描、跳过游戏写到一半的 temp.wowsreplay、跳过仍在写入的
/// 文件、跳过文件名不合规的文件。
///
/// 用静态方法而不是实例，是因为观测器需要每次调用传入不同的稳定阈值：
/// 日常轮询可以等 5 秒，启动时的补录不该让刚打完的一局被多等一轮。
/// </remarks>
public static class ReplayDirectoryScanner
{
    /// <summary>游戏用于临时落盘的文件名，写完才改名，不算正式回放。</summary>
    public const string TemporaryFileName = "temp.wowsreplay";

    /// <summary>
    /// 扫一遍回放目录。
    /// </summary>
    /// <param name="root">回放根目录。目录不存在时返回空。</param>
    /// <param name="minIdle">文件距最后一次写入至少空闲多久才算写完。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按开战时间倒序排列的回放文件。</returns>
    public static IReadOnlyList<ReplayFileInfo> Scan(
        string? root,
        TimeSpan minIdle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var results = new List<ReplayFileInfo>();

        foreach (string path in EnumerateReplayPaths(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Path.GetFileName(path).Equals(TemporaryFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ReplayFileName.TryParseTimestamp(path, out var startedAt))
            {
                continue;
            }

            FileInfo? info = TryGetInfo(path);
            if (info is null || info.Length <= 0)
            {
                continue;
            }

            var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (now - lastWrite < minIdle)
            {
                // 还在写，留给下一轮。
                continue;
            }

            results.Add(new ReplayFileInfo(path, startedAt, info.Length, lastWrite));
        }

        results.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
        return results;
    }

    /// <summary>递归枚举回放路径。目录不可读时返回空而不是抛异常。</summary>
    private static IReadOnlyList<string> EnumerateReplayPaths(string root)
    {
        try
        {
            // 回放按版本分子目录，必须递归。
            return Directory.GetFiles(root, "*.wowsreplay", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static FileInfo? TryGetInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
