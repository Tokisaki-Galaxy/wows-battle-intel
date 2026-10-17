using System.IO;
using System.Runtime.CompilerServices;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 测试进程退出前清掉临时目录里的残留。
/// </summary>
/// <remarks>
/// 单个测试的 <see cref="TestServices.Dispose"/> 已经负责删自己建的文件，
/// 但测试失败、断言中断或进程被强制结束时，那段代码不一定跑到。日积月累
/// 会留下上万个数据库文件，占掉几个 GB。
///
/// 这里挂一个进程级的兜底：不论前面发生了什么，进程正常退出前把
/// <c>.test-tmp</c> 下本进程产生的文件清空。删除失败只忽略，不影响结论。
/// </remarks>
internal static class TestTempCleanup
{
    private static bool registered;

    private static readonly object Gate = new();

    /// <summary>
    /// 注册退出清理。重复调用无副作用。
    /// </summary>
    /// <remarks>
    /// 挂在 <see cref="AppDomain.ProcessExit"/> 上而不是 xUnit 的集合清理，
    /// 因为后者只对打了标记的集合生效，覆盖不到所有测试类。
    /// </remarks>
    [ModuleInitializer]
    internal static void Register()
    {
        lock (Gate)
        {
            if (registered)
            {
                return;
            }

            registered = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        }
    }

    /// <summary>
    /// 删除临时目录里的数据库与日志残留。
    /// </summary>
    /// <remarks>
    /// 只删本工具命名的文件，不动编译输出目录 build 与别人的东西。
    /// </remarks>
    private static void Cleanup()
    {
        string root = TestServices.TestTempRoot;
        if (!Directory.Exists(root))
        {
            return;
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (string pattern in new[] { "wbi-*.db", "wbi-*.db-wal", "wbi-*.db-shm" })
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string file in files)
            {
                DeleteFile(file);
            }
        }

        // 落盘失败用例会故意把目录换成同名占位文件，这类文件不在 db 后缀里。
        foreach (string pattern in new[] { "wbi-log-test-*", "wbi-custom-*" })
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string file in files)
            {
                DeleteFile(file);
            }
        }

        // 落盘失败用例会故意把目录换成同名占位文件，这类文件不在 db 后缀里。
        foreach (string pattern in new[] { "wbi-log-test-*", "wbi-custom-*" })
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string file in files)
            {
                DeleteFile(file);
            }
        }

        // 测试还会建一批带 wbi- 前缀的临时目录，装日志或假游戏目录，一并清掉。
        foreach (string directory in Directory.EnumerateDirectories(root, "wbi-*"))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 目录被占用时留着，不影响后续测试。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }

    /// <summary>删除单个文件，被占用时静默跳过。</summary>
    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 还被占用就留着，下次运行再清。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
