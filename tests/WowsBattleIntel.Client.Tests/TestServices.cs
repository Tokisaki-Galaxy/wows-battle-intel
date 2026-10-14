using System.IO;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Client.Services;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 测试用的服务装配。
/// </summary>
/// <remarks>
/// 用临时文件数据库而非内存库：仓储每次操作开短连接，内存库在连接关闭后
/// 会丢失数据，临时文件更贴近真实行为。测试结束负责清理。
/// </remarks>
internal sealed class TestServices : IDisposable
{
    private readonly List<string> databasePaths = [];

    private TestServices(AppServices services)
    {
        Services = services;
    }

    /// <summary>装配好的服务。</summary>
    public AppServices Services { get; }

    /// <summary>
    /// 创建一套指向全新临时数据库的服务。
    /// </summary>
    /// <remarks>
    /// 测试期间把配置路径重定向到临时文件，避免测试写盘覆盖真实的
    /// %APPDATA% 配置。
    /// </remarks>
    public static async Task<TestServices> CreateAsync(AppConfig? config = null)
    {
        ConfigPathGuard.Acquire();

        config ??= new AppConfig();
        if (string.IsNullOrWhiteSpace(config.DatabasePath))
        {
            config.DatabasePath = NewDatabasePath();
        }

        var services = await AppServices.CreateAsync(config);
        var holder = new TestServices(services);
        holder.databasePaths.Add(config.DatabasePath);
        return holder;
    }

    /// <summary>生成一个未被占用的临时数据库路径。</summary>
    public static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"wbi-client-test-{Guid.NewGuid():N}.db");

    /// <inheritdoc />
    public void Dispose()
    {
        Services.Dispose();
        ConfigPathGuard.Release();

        foreach (string path in databasePaths)
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响测试结论。
        }
    }
}

/// <summary>
/// 测试期间的配置路径守卫。
/// </summary>
/// <remarks>
/// 把 AppConfig 的读写重定向到临时文件，防止测试写盘覆盖真实的
/// %APPDATA%\wows-battle-intel\config.json。重定向支持嵌套：
/// 每层保存并恢复上一层的路径值。
/// </remarks>
internal static class ConfigPathGuard
{
    /// <summary>
    /// 进入测试作用域。安装一个新的临时配置路径。
    /// </summary>
    public static void Acquire()
    {
        AppConfig.ConfigPathOverride = Path.Combine(
            Path.GetTempPath(),
            $"wbi-config-test-{Guid.NewGuid():N}.json");
    }

    /// <summary>
    /// 离开测试作用域。删除临时文件并解除重定向。
    /// </summary>
    public static void Release()
    {
        string? path = AppConfig.ConfigPathOverride;
        AppConfig.ConfigPathOverride = null;

        if (path is not null)
        {
            TryDeleteFile(path);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响测试结论。
        }
    }
}
