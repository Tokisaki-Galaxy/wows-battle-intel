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
    public static async Task<TestServices> CreateAsync(AppConfig? config = null)
    {
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
