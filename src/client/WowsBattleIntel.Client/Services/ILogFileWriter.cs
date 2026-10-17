using WowsBattleIntel.Client.Configuration;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 日志落盘选项。
/// </summary>
/// <remarks>
/// 从配置生成后不可变，写入器在清理时按这份快照判定，不与配置对象共享状态。
/// 天数与体积都是 0 时不清理，磁盘占用会持续增长。
/// </remarks>
/// <param name="Directory">日志目录。</param>
/// <param name="RetentionDays">保留天数，0 表示不限天数。</param>
/// <param name="MaxBytes">总量上限字节，0 表示不限体积。</param>
/// <param name="Mode">清理口径，决定天数与体积是否同时生效。</param>
public sealed record LogFileOptions(
    string Directory,
    int RetentionDays,
    long MaxBytes,
    LogRetentionMode Mode)
{
    /// <summary>体积上限的展示文本，用于界面提示。</summary>
    public string MaxSizeText => MaxBytes <= 0
        ? "不限"
        : $"{MaxBytes / 1024.0 / 1024.0:0.#} MB";
}

/// <summary>
/// 日志落盘接口。
/// </summary>
/// <remarks>
/// 和 <see cref="LogService"/> 分开，让进程内缓冲可以脱离磁盘单测。
/// 实现方必须自行吞掉所有异常，写日志失败不得影响业务线程。
/// </remarks>
public interface ILogFileWriter : IDisposable
{
    /// <summary>当前日志目录。</summary>
    string Directory { get; }

    /// <summary>当日滚动文件路径，跨天后指向新文件。</summary>
    string CurrentFile { get; }

    /// <summary>写一条日志。实现方不得抛异常。</summary>
    void Write(LogEntry entry);

    /// <summary>立即刷新缓冲，用于退出前落盘。</summary>
    void Flush();

    /// <summary>按指定选项清理过期文件，含当日文件超限时的裁剪。</summary>
    /// <remarks>
    /// 只借口径，落盘目录始终以写入器自身为准，避免手改未保存的路径误删别处文件。
    /// 实现方不得抛异常。
    /// </remarks>
    void Trim(LogFileOptions limits);

    /// <summary>当前使用的选项。</summary>
    LogFileOptions Options { get; }
}
