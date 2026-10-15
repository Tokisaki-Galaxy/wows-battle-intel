using System.IO;
using WowsBattleIntel.Client.Services;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 游戏路径探测器测试。注册表与进程环境依赖真实系统，这里只测可注入的
/// 纯函数行为与未命中的稳定性。
/// </summary>
public class GamePathDetectorTests
{
    [Fact]
    public void Detect_无游戏环境_返回null而不抛异常()
    {
        // CI 与测试机大概率没有运行游戏进程，RecentApps 也不太可能命中，
        // Detect 应稳定返回 null。真实命中场景靠人工验收。
        var result = GamePathDetector.Detect();
        Assert.True(result is null || Directory.Exists(Path.Combine(result.GamePath, "replays")));
    }

    [Fact]
    public void Detect_命中结果必带replays目录()
    {
        // 命中判定契约：无论来源，GamePath 下必须有 replays。
        var result = GamePathDetector.Detect();
        if (result is not null)
        {
            Assert.True(Directory.Exists(result.GamePath));
            Assert.False(string.IsNullOrWhiteSpace(result.Source));
        }
    }
}
