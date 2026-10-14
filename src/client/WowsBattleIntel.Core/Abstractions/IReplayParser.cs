using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Abstractions;

/// <summary>
/// 回放解析器。把 .wowsreplay 文件解析成结构化的战斗记录。
/// </summary>
/// <remarks>
/// 这个接口刻意与具体实现解耦。近期用外部 replayshark 进程实现，
/// 长期可换成进程内的纯 C# 库实现，上层代码不受影响。
/// </remarks>
public interface IReplayParser
{
    /// <summary>解析器名称，用于日志与诊断。</summary>
    string Name { get; }

    /// <summary>
    /// 判断某个回放文件是否受当前解析器支持。
    /// 用于版本不匹配时提前跳过，而不是解析到一半失败。
    /// </summary>
    Task<bool> CanParseAsync(string replayPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析回放文件。
    /// </summary>
    /// <param name="replayPath">回放文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析出的战斗记录。解析失败时抛 <see cref="ReplayParseException"/>。</returns>
    Task<BattleRecord> ParseAsync(string replayPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// 回放解析失败。
/// </summary>
public sealed class ReplayParseException : Exception
{
    public ReplayParseException(string message) : base(message)
    {
    }

    public ReplayParseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
