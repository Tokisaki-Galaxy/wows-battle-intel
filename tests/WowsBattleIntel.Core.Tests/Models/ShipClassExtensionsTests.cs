using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Tests.ShipClasses;

/// <summary>
/// 舰种解析的回归测试。船目录里航母写作 AirCarrier（无下划线），
/// 旧实现只认 AIR_CARRIER，导致所有航母落到 Unknown 显示 "??"。
/// </summary>
public class ShipClassExtensionsTests
{
    [Theory]
    // 船目录使用的写法（无下划线），白龙等航母的舰种即此类。
    [InlineData("AirCarrier", ShipClass.Carrier)]
    [InlineData("aircarrier", ShipClass.Carrier)]
    // 兼容历史与 API 的下划线写法。
    [InlineData("AIR_CARRIER", ShipClass.Carrier)]
    [InlineData("carrier", ShipClass.Carrier)]
    [InlineData("CV", ShipClass.Carrier)]
    // 其余舰种不受影响。
    [InlineData("Destroyer", ShipClass.Destroyer)]
    [InlineData("Cruiser", ShipClass.Cruiser)]
    [InlineData("Battleship", ShipClass.Battleship)]
    [InlineData("Submarine", ShipClass.Submarine)]
    [InlineData("DD", ShipClass.Destroyer)]
    [InlineData("BB", ShipClass.Battleship)]
    [InlineData("SS", ShipClass.Submarine)]
    public void Parse_NormalizesCommonSpellings(string shipType, ShipClass expected)
    {
        Assert.Equal(expected, ShipClassExtensions.Parse(shipType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mystery")]
    public void Parse_UnknownOrEmpty_ReturnsUnknown(string? shipType)
    {
        Assert.Equal(ShipClass.Unknown, ShipClassExtensions.Parse(shipType));
    }
}
