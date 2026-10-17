namespace WowsBattleIntel.Client.Configuration;

/// <summary>
/// 日志文件清理口径。
/// </summary>
public enum LogRetentionMode
{
    /// <summary>天数与体积两个条件同时生效，任一超出即清理。默认口径。</summary>
    Both,

    /// <summary>只按天数清理，不看体积。</summary>
    Days,

    /// <summary>只按体积清理，不看天数。</summary>
    Size,
}
