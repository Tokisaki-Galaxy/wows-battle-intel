using WowsBattleIntel.Client.Services;

namespace WowsBattleIntel.Client.Tests;

public class ReplayServiceTests
{
    [Theory]
    [InlineData(@"D:\replays\15.6.0.0\20260722_132902_test.wowsreplay", "15.6.0.0")]
    [InlineData(@"D:\replays\15.7.0.0\20260813_110112_x.wowsreplay", "15.7.0.0")]
    [InlineData(@"C:\game\replays\15.8.0.0\file.wowsreplay", "15.8.0.0")]
    [InlineData(@"D:\replays\20260722_test.wowsreplay", null)]
    [InlineData(@"D:\replays\no_version_here.wowsreplay", null)]
    public void ExtractVersion_ParsesFourSegmentVersion(string path, string? expected)
    {
        string? actual = ReplayService.ExtractVersion(path);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExtractVersion_HandlesMultipleVersions_TakesFirst()
    {
        // 路径中出现多个版本号时，取第一个（目录名那个）。
        string? actual = ReplayService.ExtractVersion(@"D:\15.6.0.0\foo\1.2.3.4\file.wowsreplay");
        Assert.Equal("15.6.0.0", actual);
    }
}
