using System.IO;
using WowsBattleIntel.Client.Configuration;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// AppConfig 持久化行为测试。
/// </summary>
/// <remarks>
/// 全部用例在 ConfigPathGuard 重定向后的临时路径上读写，
/// 不触碰真实的 %APPDATA% 配置。
/// </remarks>
public class AppConfigTests
{
    [Fact]
    public void Load_MissingFile_WritesDefaultAndInfersPaths()
    {
        using var scope = new ConfigPathScope();

        Assert.False(File.Exists(AppConfig.ConfigPath));

        var config = AppConfig.Load();

        // 默认配置应已写盘。
        Assert.True(File.Exists(AppConfig.ConfigPath));
        // 数据库路径始终有推断值，且为绝对路径。
        Assert.False(string.IsNullOrWhiteSpace(config.DatabasePath));
        Assert.True(Path.IsPathRooted(config.DatabasePath));
    }

    [Fact]
    public void Load_ExistingFile_KeepsUserValues()
    {
        using var scope = new ConfigPathScope();

        var original = new AppConfig
        {
            GamePath = @"D:\SteamLibrary\steamapps\common\World of Warships",
            DatabasePath = @"E:\my-battles.db",
            ModelPath = @"E:\my-model.onnx",
        };
        original.Save();

        var loaded = AppConfig.Load();

        // 已有配置的用户值必须原样保留，默认推断不得覆盖。
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\World of Warships", loaded.GamePath);
        Assert.Equal(@"E:\my-battles.db", loaded.DatabasePath);
        Assert.Equal(@"E:\my-model.onnx", loaded.ModelPath);
    }

    [Fact]
    public void Load_RelativePaths_AreAbsolutized()
    {
        using var scope = new ConfigPathScope();

        var original = new AppConfig
        {
            DatabasePath = "data\\battles.db",
            ModelPath = "data\\models\\baseline.onnx",
        };
        original.Save();

        var loaded = AppConfig.Load();

        // 相对路径按当前工作目录绝对化，不依赖启动入口的工作目录。
        Assert.Equal(Path.GetFullPath("data\\battles.db"), loaded.DatabasePath);
        Assert.Equal(Path.GetFullPath("data\\models\\baseline.onnx"), loaded.ModelPath);
    }

    [Fact]
    public void Load_TestArtifactDatabasePath_IsReinferred()
    {
        using var scope = new ConfigPathScope();

        // 模拟此前测试污染过的真实配置：临时库路径与空游戏目录。
        var polluted = new AppConfig
        {
            GamePath = string.Empty,
            DatabasePath = Path.Combine(
                Path.GetTempPath(), "wbi-client-test-2ad5f4dbdce047c3aad0c0ced1142b03.db"),
        };
        polluted.Save();

        var loaded = AppConfig.Load();

        // 测试遗留的临时库路径应重新推断，游戏目录空值时探测。
        Assert.DoesNotContain("wbi-client-test-", loaded.DatabasePath);
        Assert.False(string.IsNullOrWhiteSpace(loaded.DatabasePath));
    }

    [Fact]
    public void ReplayDirectory_GamePathEmpty_FallsBackToFixedDefault()
    {
        var config = new AppConfig { GamePath = string.Empty };

        // 未配置游戏目录时回放目录退回固定位置，不产生依赖工作目录的相对路径。
        Assert.Equal(@"D:\replays", config.ReplayDirectory);
        Assert.True(Path.IsPathRooted(config.ReplayDirectory));
    }

    [Fact]
    public void ReplayDirectory_GamePathSet_CombinesReplaysSubdirectory()
    {
        var config = new AppConfig
        {
            GamePath = @"D:\SteamLibrary\steamapps\common\World of Warships",
        };

        Assert.Equal(
            Path.Combine(@"D:\SteamLibrary\steamapps\common\World of Warships", "replays"),
            config.ReplayDirectory);
    }

    [Fact]
    public void IsUsable_ReplayDirectoryExists_ReturnsTrue()
    {
        var config = new AppConfig { GamePath = @"D:\replays\game-root" };
        Directory.CreateDirectory(config.ReplayDirectory);
        try
        {
            Assert.True(config.IsUsable);
        }
        finally
        {
            Directory.Delete(config.ReplayDirectory);
            Directory.Delete(@"D:\replays\game-root");
        }
    }
}

/// <summary>
/// 单个测试用例专用的配置路径隔离作用域。
/// </summary>
/// <remarks>
/// ConfigPathGuard 支持计数复用，这里套一层 IDisposable 方便单用例隔离。
/// </remarks>
internal sealed class ConfigPathScope : IDisposable
{
    /// <summary>进入作用域，启用配置路径重定向。</summary>
    public ConfigPathScope() => ConfigPathGuard.Acquire();

    /// <inheritdoc />
    public void Dispose() => ConfigPathGuard.Release();
}
