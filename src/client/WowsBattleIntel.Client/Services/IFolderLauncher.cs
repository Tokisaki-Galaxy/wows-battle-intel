namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 在文件资源管理器中打开目录。
/// </summary>
/// <remarks>
/// 抽象出来是为了让设置视图模型可测，不必真的拉起 explorer。
/// </remarks>
public interface IFolderLauncher
{
    /// <summary>
    /// 打开目录。目录不存在时先创建。
    /// </summary>
    /// <returns>成功返回 true，失败返回 false。</returns>
    bool Open(string path);
}
