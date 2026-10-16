using System.IO;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 服务器自动探测测试。复刻 ApeRadar 从 clientrunner.log 读 realm 的口径。
/// </summary>
public class ServerResolverTests : IDisposable
{
    private readonly string gamePath;

    public ServerResolverTests()
    {
        gamePath = Path.Combine(TestServices.TestTempRoot, $"wbi-srv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(gamePath, "profile"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(gamePath, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论。
        }
    }

    private void WriteLog(params string[] lines) =>
        File.WriteAllLines(Path.Combine(gamePath, "profile", "clientrunner.log"), lines);

    [Theory]
    [InlineData("asia", GameServer.Asia)]
    [InlineData("eu", GameServer.Eu)]
    [InlineData("na", GameServer.Na)]
    [InlineData("ru", GameServer.Ru)]
    [InlineData("cn", GameServer.Cn)]
    public void Resolve_MapsRealm(string realm, GameServer expected)
    {
        WriteLog(
            "[2026-10-16 07:44:21] [  INFO  ] Selected realm: eu",
            $"[2026-10-16 11:44:07] [  INFO  ] Selected realm: {realm}");

        // 取最后一次，覆盖启动初期选错的临时值。
        Assert.Equal(expected, ServerResolver.Resolve(gamePath));
    }

    [Fact]
    public void Resolve_RealLogFormat_ReturnsAsia()
    {
        WriteLog(
            "[2026-10-16 13:15:22] [  INFO  ] Selected realm: asia",
            "[2026-10-16 13:15:22] [  INFO  ] Multiversion client detected",
            "[2026-10-16 13:15:23] [  INFO  ] Detected server version 15.9.0.13357625");

        Assert.Equal(GameServer.Asia, ServerResolver.Resolve(gamePath));
    }

    [Fact]
    public void Resolve_MissingLog_ReturnsAuto()
    {
        Assert.Equal(GameServer.Auto, ServerResolver.Resolve(gamePath));
    }

    [Fact]
    public void Resolve_EmptyPath_ReturnsAuto()
    {
        Assert.Equal(GameServer.Auto, ServerResolver.Resolve(string.Empty));
    }
}
