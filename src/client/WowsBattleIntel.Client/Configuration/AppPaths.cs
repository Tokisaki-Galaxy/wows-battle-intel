using System.IO;

namespace WowsBattleIntel.Client.Configuration;

/// <summary>
/// 应用在用户目录下的固定路径。
/// </summary>
/// <remarks>
/// 配置、数据库、日志与更新节流时间戳都落在 %APPDATA%\wows-battle-intel 下。
/// 此前这个目录名在配置解析与更新服务里各拼了一遍，改名或换根目录容易漏改，
/// 统一收在这里。
/// </remarks>
public static class AppPaths
{
    /// <summary>应用在漫游应用数据下的目录名。</summary>
    public const string AppDataFolderName = "wows-battle-intel";

    /// <summary>用户漫游应用数据下的应用根目录。</summary>
    public static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppDataFolderName);

    /// <summary>在应用根目录下拼一个相对路径。</summary>
    public static string CombineAppData(params string[] segments) =>
        Path.Combine([AppDataDirectory, .. segments]);
}
