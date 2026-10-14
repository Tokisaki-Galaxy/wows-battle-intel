using WowsBattleIntel.Client.ViewModels;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Tests;

public class PlayerRowViewModelTests
{
    private static PlayerRecord MakePlayer(
        string name,
        double winrate,
        double battles,
        bool hidden = false,
        ShipClass shipClass = ShipClass.Battleship,
        int tier = 10,
        int divisionSize = 1)
    {
        return new PlayerRecord
        {
            AccountId = "1",
            PlayerName = name,
            AccountWinrate = hidden ? -1 : winrate,
            AccountBattles = hidden ? -1 : battles,
            IsHidden = hidden,
            ShipName = "TestShip",
            ShipId = "TEST001",
            ShipClass = shipClass,
            ShipTier = tier,
            DivisionSize = divisionSize,
        };
    }

    [Fact]
    public void FromPlayer_FormatsWinrateAsPercent()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.567, 1000));
        Assert.Equal("56.7%", vm.WinrateText);
    }

    [Fact]
    public void FromPlayer_HiddenPlayer_ShowsHiddenLabel()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0, 0, hidden: true));
        Assert.Equal("隐藏", vm.WinrateText);
        Assert.Equal("隐藏", vm.TierLabel);
        Assert.True(vm.IsHidden);
    }

    [Fact]
    public void FromPlayer_HighWinrate_ClassifiedAsUnicum()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.62, 1000));
        Assert.Equal("海王", vm.TierLabel);
    }

    [Fact]
    public void FromPlayer_LowWinrate_ClassifiedAsApe()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.45, 1000));
        Assert.Equal("猴子", vm.TierLabel);
    }

    [Fact]
    public void FromPlayer_MiddleWinrate_ClassifiedAsNormal()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.52, 1000));
        Assert.Equal("普通", vm.TierLabel);
    }

    [Fact]
    public void FromPlayer_HighWinrateButFewBattles_NotUnicum()
    {
        // 胜率虽高但场次不足，不判为海王。
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.62, 100));
        Assert.Equal("普通", vm.TierLabel);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(2, "双排")]
    [InlineData(3, "三排")]
    public void FromPlayer_DivisionMark(int size, string expected)
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.5, 100, divisionSize: size));
        Assert.Equal(expected, vm.DivisionMark);
    }

    [Theory]
    [InlineData(ShipClass.Destroyer, "DD 10")]
    [InlineData(ShipClass.Cruiser, "CA 10")]
    [InlineData(ShipClass.Battleship, "BB 10")]
    [InlineData(ShipClass.Carrier, "CV 10")]
    [InlineData(ShipClass.Submarine, "SS 10")]
    public void FromPlayer_ShipTypeLabel(ShipClass shipClass, string expected)
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.5, 100, shipClass: shipClass));
        Assert.Equal(expected, vm.ShipTypeText);
    }

    [Fact]
    public void FromPlayer_ZeroDamage_ShowsDash()
    {
        var player = MakePlayer("A", 0.5, 100);
        player.DamageDealt = 0;
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal("-", vm.DamageText);
    }

    [Fact]
    public void FromPlayer_WithDamage_ShowsFormattedNumber()
    {
        var player = MakePlayer("A", 0.5, 100);
        player.DamageDealt = 81230;
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal("81,230", vm.DamageText);
    }
}
