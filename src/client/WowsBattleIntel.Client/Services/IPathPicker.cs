namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 路径选择器。
/// </summary>
/// <remarks>
/// 把文件与目录选择对话框抽象成接口，视图模型就不必依赖 WPF 的对话框类，
/// 单元测试可以注入一个返回固定路径的替身。
/// </remarks>
public interface IPathPicker
{
    /// <summary>
    /// 选择目录。用户取消时返回 null。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="currentPath">当前路径，作为初始位置。</param>
    string? PickFolder(string title, string currentPath);

    /// <summary>
    /// 选择文件。用户取消时返回 null。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="filter">文件类型过滤器，格式见 WPF 约定。</param>
    /// <param name="currentPath">当前路径，作为初始位置。</param>
    string? PickFile(string title, string filter, string currentPath);
}
