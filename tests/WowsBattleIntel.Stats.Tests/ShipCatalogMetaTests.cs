using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Stats.Tests;

/// <summary>
/// 船目录元数据回填的回归测试。tempArenaInfo 15.9 的 vehicles 不再带
/// shipType/shipTier，舰种与等级全靠船目录回填。目录缺条目时舰种落到
/// Unknown，界面显示 "??"。
/// </summary>
public class ShipCatalogMetaTests
{
    [Theory]
    // 15.9 实测名单里的航母，曾因目录版本过旧（12.5）而查不到。
    [InlineData("4179605200", "白龙", 10, ShipClass.Carrier)]
    // 目录缺失的当季新船，补录后应命中。
    [InlineData("4284396912", "刻耳柏洛斯", 10, ShipClass.Cruiser)]
    [InlineData("3550426576", "西伯利亚", 10, ShipClass.Battleship)]
    [InlineData("3434526416", "BA 阿罗娜的小鲸鱼", 10, ShipClass.Submarine)]
    // 常规条目。
    [InlineData("4276041424", "大和", 10, ShipClass.Battleship)]
    public void TryGetMeta_KnownShip_ReturnsNameTierAndClass(
        string shipId, string expectedName, int expectedTier, ShipClass expectedClass)
    {
        bool ok = ShipCatalog.TryGetMeta(shipId, out var meta);

        Assert.True(ok);
        Assert.NotNull(meta);
        Assert.Equal(expectedName, meta!.ChineseName);
        Assert.Equal(expectedTier, meta.Tier);
        Assert.Equal(expectedClass, ShipClassExtensions.Parse(meta.Type));
    }

    [Fact]
    public void TryGetMeta_UnknownId_ReturnsFalse()
    {
        Assert.False(ShipCatalog.TryGetMeta("9999999999", out var meta));
        Assert.Null(meta);
    }
}
