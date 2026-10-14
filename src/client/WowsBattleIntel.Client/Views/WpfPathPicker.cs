using System.IO;
using Microsoft.Win32;
using WowsBattleIntel.Client.Services;

namespace WowsBattleIntel.Client.Views;

/// <summary>
/// 基于 WPF 对话框的路径选择器。
/// </summary>
/// <remarks>
/// WPF 没有内置的目录选择对话框，改用 OpenFileDialog 的文件夹选择模式，
/// 避免为了一个对话框引入额外依赖。
/// </remarks>
public sealed class WpfPathPicker : IPathPicker
{
    /// <inheritdoc />
    public string? PickFolder(string title, string currentPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath))
        {
            dialog.InitialDirectory = currentPath;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <inheritdoc />
    public string? PickFile(string title, string filter, string currentPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };

        string? directory = Path.GetDirectoryName(currentPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
            dialog.FileName = Path.GetFileName(currentPath);
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
