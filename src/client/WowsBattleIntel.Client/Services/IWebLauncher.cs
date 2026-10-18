using System.Diagnostics;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 用系统默认浏览器打开网址。
/// </summary>
/// <remarks>
/// 抽象出来是为了让名单行视图模型可测，不必真的拉起浏览器。
/// </remarks>
public interface IWebLauncher
{
    /// <summary>
    /// 用默认方式打开一个网址。
    /// </summary>
    /// <returns>成功返回 true，失败返回 false。</returns>
    bool Open(string url);
}

/// <summary>
/// 用 shell 默认程序打开网址，系统会交给用户的默认浏览器。
/// </summary>
public sealed class ShellWebLauncher : IWebLauncher
{
    /// <inheritdoc />
    public bool Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            };
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception)
        {
            // 浏览器拉不起来属于环境问题，调用方提示一句即可。
            return false;
        }
    }
}
