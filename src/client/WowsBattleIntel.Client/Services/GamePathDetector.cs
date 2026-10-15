using System.Diagnostics;
using System.IO;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 游戏安装目录自动探测。
/// </summary>
/// <remarks>
/// 探测顺序（参照 ApeRadar 的 AutoDetectGamePath 并加强）：
/// 1. 运行中的游戏进程，路径最实时，必然指向真实安装。
/// 2. 卸载注册表的 InstallLocation，按 Wargaming 系发布者过滤，覆盖
///    WG Game Center 与 360 国服安装器。游戏未运行也能找到，是常态下的
///    主力来源。
/// 命中判定统一要求 replays 子目录存在，避免探测到启动器或残缺安装。
/// </remarks>
public static class GamePathDetector
{
    /// <summary>候选进程名。国服与国际服主程序与启动器都算。</summary>
    private static readonly string[] ProcessNames =
    [
        "WorldOfWarships",
        "WorldOfWarships32",
        "WoWSLauncher",
    ];

    /// <summary>游戏主程序文件名，卸载注册表与进程路径的最终校验依据。</summary>
    private const string GameExe = "WorldOfWarships.exe";

    /// <summary>Wargaming 系发布者名单，与 ApeRadar 口径一致。</summary>
    private static readonly string[] Publishers =
    [
        "Wargaming.net",
        "Wargaming Group Limited",
        "360.cn",
        "Lesta Games",
    ];

    /// <summary>探测结果。</summary>
    public sealed record GamePathResult(string GamePath, string Source);

    /// <summary>
    /// 探测游戏安装目录，未命中返回 null。
    /// </summary>
    public static GamePathResult? Detect()
    {
        return DetectFromRunningProcess() ?? DetectFromUninstallRegistry();
    }

    /// <summary>
    /// 从运行中的游戏进程定位。进程 exe 所在目录向上找 replays。
    /// </summary>
    private static GamePathResult? DetectFromRunningProcess()
    {
        foreach (var name in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception)
            {
                // 进程枚举被安全软件拦截时跳过该候选名。
                continue;
            }

            foreach (var process in processes)
            {
                string? exePath;
                try
                {
                    // 64 位进程读 32 位进程路径可能失败，逐个容错。
                    exePath = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    continue;
                }
                finally
                {
                    process.Dispose();
                }

                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    continue;
                }

                var root = FindGameRoot(Path.GetDirectoryName(exePath));
                if (root is not null)
                {
                    return new GamePathResult(root, $"运行中的进程 {name}");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 从卸载注册表定位。遍历 HKCU 与 HKLM 的 Uninstall 键，按发布者
    /// 过滤出 Wargaming 系产品，InstallLocation 下存在游戏主程序且
    /// replays 目录真实存在者命中。
    /// </summary>
    private static GamePathResult? DetectFromUninstallRegistry()
    {
        var roots = new[]
        {
            (Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            // 32 位程序在 64 位系统上的转视角注册表，部分老安装器写这里。
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, path) in roots)
        {
            using var baseKey = hive.OpenSubKey(path);
            if (baseKey is null)
            {
                continue;
            }

            foreach (var subName in baseKey.GetSubKeyNames())
            {
                using var key = baseKey.OpenSubKey(subName);
                if (key is null)
                {
                    continue;
                }

                var publisher = key.GetValue("Publisher") as string;
                if (publisher is null
                    || !Publishers.Any(p => publisher.Equals(p, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var installLocation = key.GetValue("InstallLocation") as string;
                if (string.IsNullOrWhiteSpace(installLocation))
                {
                    continue;
                }

                // 去掉安装器可能带的尾随反斜杠与引号。
                installLocation = installLocation.Trim().Trim('"', '\\');
                if (!File.Exists(Path.Combine(installLocation, GameExe)))
                {
                    continue;
                }

                var root = FindGameRoot(installLocation);
                if (root is not null)
                {
                    return new GamePathResult(root, $"注册表卸载信息 {publisher}");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 从目录向上找游戏根：replays 目录所在层。最多向上走四层，兼容
    /// bin/xxxx 主程序布局与安装器直接指到产品目录的情况。
    /// </summary>
    private static string? FindGameRoot(string? startDir)
    {
        if (string.IsNullOrWhiteSpace(startDir))
        {
            return null;
        }

        var dir = new DirectoryInfo(startDir);
        for (int depth = 0; depth < 4 && dir is not null; depth++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "replays")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
