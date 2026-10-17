using System.IO;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Client.Services;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 回放解析服务的用例。
/// </summary>
/// <remarks>
/// 数据源改为游戏安装目录后，服务只负责校验目录是否就绪并把路径传给解析器，
/// 不再做版本推断，因此这里覆盖的是这两个判断。
/// </remarks>
public class ReplayServiceTests
{
    [Fact]
    public void CanParse_FalseWhenGamePathMissing()
    {
        string replay = CreateReplay();
        try
        {
            var config = new AppConfig { GamePath = string.Empty };
            var service = new ReplayService(config);

            Assert.False(service.CanParse(replay));
        }
        finally
        {
            File.Delete(replay);
        }
    }

    [Fact]
    public void CanParse_FalseWhenGamePathDoesNotExist()
    {
        string replay = CreateReplay();
        try
        {
            var config = new AppConfig
            {
                GamePath = Path.Combine(Path.GetTempPath(), $"wbi-no-such-game-{Guid.NewGuid():N}"),
            };
            var service = new ReplayService(config);

            Assert.False(service.CanParse(replay));
        }
        finally
        {
            File.Delete(replay);
        }
    }

    [Fact]
    public void CanParse_FalseForNonReplayExtension()
    {
        string gameDir = CreateGameDirectory();
        string file = Path.Combine(TestServices.TestTempRoot, $"wbi-note-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");
        try
        {
            var service = new ReplayService(new AppConfig { GamePath = gameDir });

            Assert.False(service.CanParse(file));
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(gameDir, recursive: true);
        }
    }

    [Fact]
    public void CanParse_TrueWhenGameDirectoryExists()
    {
        string gameDir = CreateGameDirectory();
        string replay = Path.Combine(gameDir, "replays", "20261016_test.wowsreplay");
        File.WriteAllBytes(replay, []);
        try
        {
            var service = new ReplayService(new AppConfig { GamePath = gameDir });

            Assert.True(service.CanParse(replay));
        }
        finally
        {
            Directory.Delete(gameDir, recursive: true);
        }
    }

    [Fact]
    public async Task ParseWithoutGamePath_ThrowsHelpfulError()
    {
        string replay = CreateReplay();
        try
        {
            var service = new ReplayService(new AppConfig { GamePath = string.Empty });

            var exception = await Assert.ThrowsAsync<WowsBattleIntel.Core.Abstractions.ReplayParseException>(
                () => service.ParseAsync(replay));

            Assert.Contains("游戏安装目录", exception.Message);
        }
        finally
        {
            File.Delete(replay);
        }
    }

    /// <summary>建一个带 replays 子目录的假游戏目录。</summary>
    private static string CreateGameDirectory()
    {
        string root = Path.Combine(TestServices.TestTempRoot, $"wbi-game-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "replays"));
        return root;
    }

    /// <summary>建一个空的回放文件，只用于存在性判断。</summary>
    private static string CreateReplay()
    {
        string path = Path.Combine(TestServices.TestTempRoot, $"wbi-{Guid.NewGuid():N}.wowsreplay");
        File.WriteAllBytes(path, []);
        return path;
    }
}
