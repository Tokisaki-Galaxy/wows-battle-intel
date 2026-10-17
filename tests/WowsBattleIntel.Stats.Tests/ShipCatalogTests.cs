namespace WowsBattleIntel.Stats.Tests;

public class ShipCatalogTests
{
    [Fact]
    public void TryGetChineseName_KnownId_ReturnsChineseName()
    {
        // 4286461936 是 ships.json 里的已知条目，中文名本森。
        var ok = ShipCatalog.TryGetChineseName("4286461936", out string? name);

        Assert.True(ok);
        Assert.Equal("本森", name);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9999999999")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetChineseName_UnknownOrInvalidId_ReturnsFalse(string shipId)
    {
        // 词典没有的 ID 一律 false，中文出参为 null，调用方走英文回落。
        var ok = ShipCatalog.TryGetChineseName(shipId, out string? name);

        Assert.False(ok);
        Assert.Null(name);
    }

    [Fact]
    public void TryGet_MatchesMainLookup()
    {
        // 可选入口与主查询同一口径。
        var ok = ShipCatalog.TryGet("4286461936", out string? name);

        Assert.True(ok);
        Assert.Equal("本森", name);
    }

    [Fact]
    public void TryGetChineseName_LegacyIndexCode_ReturnsFalse()
    {
        // 存量库里的旧索引码（如 PJSD012）不是数字 ID，查不到属预期回落。
        var ok = ShipCatalog.TryGetChineseName("PJSD012", out string? name);

        Assert.False(ok);
        Assert.Null(name);
    }

    [Theory]
    [InlineData("Shimakaze", "岛风")]
    [InlineData("shimakaze", "岛风")]
    [InlineData("  Shimakaze  ", "岛风")]
    public void TryGetChineseNameByEnglish_MatchesCaseAndWhitespace(string english, string expected)
    {
        // 回放导出的船名是英文，反查表要能吃掉大小写与首尾空白的差异。
        var ok = ShipCatalog.TryGetChineseNameByEnglish(english, out string? name);

        Assert.True(ok);
        Assert.Equal(expected, name);
    }

    [Fact]
    public void Resolve_PrefersIdThenFallsBackToEnglish()
    {
        // 数字船 ID 优先：ID 与英文名指向不同船时，以 ID 为准。
        Assert.Equal("岛风", ShipCatalog.Resolve("4282267344", "Yamato"));

        // 没有 ID 时按英文名反查。
        Assert.Equal("岛风", ShipCatalog.Resolve(string.Empty, "Shimakaze"));
    }

    [Fact]
    public void Resolve_UnknownKeepsOriginal()
    {
        // 两边都查不到时原样返回，界面不能显示空白。
        Assert.Equal("Shinano", ShipCatalog.Resolve(string.Empty, "Shinano"));
        Assert.Equal(string.Empty, ShipCatalog.Resolve(null, null));
    }
}
