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
}
