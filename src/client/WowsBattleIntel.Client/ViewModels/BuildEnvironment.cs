using System.Diagnostics;
using System.IO;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 运行环境判定。
/// </summary>
/// <remarks>
/// 单元测试会直接构造视图模型。这类实例不应去扫本机真实的回放目录，
/// 否则测试结果取决于开发机上装了什么，界面线程也会被目录扫描拖住。
/// 这里按进程里是否加载了测试宿主判断，生产环境不会命中。
/// </remarks>
internal static class BuildEnvironment
{
    /// <summary>是否运行在单元测试宿主里。</summary>
    public static bool IsTestHost { get; } = Detect();

    private static bool Detect()
    {
        string process = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        string entry = Path.GetFileNameWithoutExtension(
            Process.GetCurrentProcess().MainModule?.ModuleName ?? string.Empty);

        return Matches(process) || Matches(entry);

        static bool Matches(string name) =>
            name.Length > 0
            && (name.Contains("testhost", StringComparison.OrdinalIgnoreCase)
                || name.Contains("testrunner", StringComparison.OrdinalIgnoreCase));
    }
}
