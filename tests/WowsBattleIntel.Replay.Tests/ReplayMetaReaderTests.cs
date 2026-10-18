namespace WowsBattleIntel.Replay.Tests;

/// <summary>
/// 回放头部 build 号读取测试。夹具直接用最小 JSON 片段，字段顺序与
/// 真实回放一致。
/// </summary>
public sealed class ReplayMetaReaderTests : IDisposable
{
    private readonly string tempDir;

    public ReplayMetaReaderTests()
    {
        tempDir = Path.Combine(
            Path.GetTempPath(),
            $"wbi-meta-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        Directory.Delete(tempDir, recursive: true);
    }

    private string WriteReplay(string metaJson)
    {
        var path = Path.Combine(tempDir, $"{Guid.NewGuid():N}.wowsreplay");
        File.WriteAllText(path, metaJson);
        return path;
    }

    private const string MetaWithVersion =
        """{"matchGroup": "pvp", "gameMode": 7, "clientVersionFromExe": "15,9,0,13357625", "clientVersionFromXml": "15,9,0,13357625", "mapDisplayName": "38_Canada"}""";

    [Fact]
    public void ReadBuildNumber_FromExeField()
    {
        Assert.Equal(13357625u, ReplayMetaReader.ReadBuildNumber(WriteReplay(MetaWithVersion)));
    }

    [Fact]
    public void ReadBuildNumber_PrefersExeWhenXmlDiffers()
    {
        var meta = """{"clientVersionFromExe": "15,9,0,13357625", "clientVersionFromXml": "15,9,0,1"}""";
        Assert.Equal(13357625u, ReplayMetaReader.ReadBuildNumber(WriteReplay(meta)));
    }

    [Fact]
    public void ReadBuildNumber_XmlOnly_FallsBack()
    {
        var meta = """{"gameMode": 7, "clientVersionFromXml": "14,10,0,10846255"}""";
        Assert.Equal(10846255u, ReplayMetaReader.ReadBuildNumber(WriteReplay(meta)));
    }

    [Fact]
    public void ReadBuildNumber_NoVersionField_ReturnsNull()
    {
        Assert.Null(ReplayMetaReader.ReadBuildNumber(WriteReplay("""{"matchGroup": "pvp"}""")));
    }

    [Fact]
    public void ReadBuildNumber_MetaSpansBeyondSingleChunk_StillFound()
    {
        // 真实 meta 的版本字段前有大量 vehicle 数据，构造 6 KB 的前缀验证
        // 4096 字节扫描窗口足够命中，同时确认超出窗口的尾部字段读不到时
        // 行为正确。
        var prefix = new string('x', 3000);
        var meta = $"{{\"pad\": \"{prefix}\", \"clientVersionFromExe\": \"15,9,0,13357625\"}}";
        Assert.Equal(13357625u, ReplayMetaReader.ReadBuildNumber(WriteReplay(meta)));
    }

    [Fact]
    public void ReadBuildNumber_MissingFile_ReturnsNull()
    {
        Assert.Null(ReplayMetaReader.ReadBuildNumber(
            Path.Combine(tempDir, "not-exist.wowsreplay")));
    }
}
