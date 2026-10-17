using System.Diagnostics;
using System.IO;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 用系统资源管理器打开目录。
/// </summary>
public sealed class ExplorerFolderLauncher : IFolderLauncher
{
    /// <inheritdoc />
    public bool Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(path);

            // UseShellExecute 让 shell 决定用什么程序打开，目录即资源管理器。
            // 路径走 ProcessStartInfo 传参，不拼命令行，避免引号转义问题。
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            };
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception)
        {
            // 资源管理器打不开属于环境问题，调用方提示一句即可。
            return false;
        }
    }
}
