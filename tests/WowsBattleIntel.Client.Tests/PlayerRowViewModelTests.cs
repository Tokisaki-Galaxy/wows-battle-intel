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

    [Fact]
    public void AvgDamageText_Unknown_ShowsDash()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.5, 100));
        Assert.Equal("-", vm.AvgDamageText);
    }

    [Fact]
    public void AvgDamageText_Known_FormatsNumber()
    {
        var player = MakePlayer("A", 0.5, 100);
        player.AccountAvgDamage = 61700;
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal("61,700", vm.AvgDamageText);
    }

    [Theory]
    [InlineData(20000, "DangerBrush")]
    [InlineData(30000, "TextPrimaryBrush")]
    [InlineData(45000, "TextPrimaryBrush")]
    [InlineData(60000, "OkBrush")]
    [InlineData(80000, "OkBrush")]
    [InlineData(90000, "UnicumBrush")]
    [InlineData(120000, "UnicumBrush")]
    public void AvgDamageBrushKey_FollowsThresholds(double damage, string expected)
    {
        var player = MakePlayer("A", 0.5, 100);
        player.AccountAvgDamage = damage;
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal(expected, vm.AvgDamageBrushKey);
    }

    [Fact]
    public void AvgDamageBrushKey_Unknown_ShowsSecondary()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.5, 100));
        Assert.Equal("TextSecondaryBrush", vm.AvgDamageBrushKey);
    }

    [Fact]
    public void ShipStats_Unknown_ShowsDashes()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.5, 100));
        Assert.Equal("-", vm.ShipBattlesText);
        Assert.Equal("-", vm.ShipWinrateText);
        Assert.Equal("-", vm.ShipDamageText);
        Assert.Equal("HiddenBrush", vm.ShipWinrateBrushKey);
    }

    [Fact]
    public void ShipStats_Known_FormatsValues()
    {
        var player = MakePlayer("A", 0.5, 100);
        player.ShipBattles = 230;
        player.ShipWinrate = 0.623;
        player.ShipAvgDamage = 71200;
        var vm = PlayerRowViewModel.FromPlayer(player);

        Assert.Equal("230", vm.ShipBattlesText);
        Assert.Equal("62.3%", vm.ShipWinrateText);
        Assert.Equal("71,200", vm.ShipDamageText);
    }

    [Theory]
    [InlineData(0.62, 600, "UnicumBrush")]
    [InlineData(0.62, 100, "TextPrimaryBrush")]
    [InlineData(0.45, 600, "ApeBrush")]
    [InlineData(0.52, 600, "TextPrimaryBrush")]
    public void ShipWinrateBrushKey_FollowsWinrateThresholds(
        double winrate, double battles, string expected)
    {
        var player = MakePlayer("A", 0.5, 100);
        player.ShipWinrate = winrate;
        player.ShipBattles = battles;
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal(expected, vm.ShipWinrateBrushKey);
    }

    [Fact]
    public void ShipDamageBrushKey_MatchesAccountThresholds()
    {
        var player = MakePlayer("A", 0.5, 100);
        player.ShipAvgDamage = 95000;
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal("UnicumBrush", vm.ShipDamageBrushKey);
    }

    [Theory]
    [InlineData(300, "WarnBrush")]
    [InlineData(999, "WarnBrush")]
    [InlineData(1000, "TextPrimaryBrush")]
    [InlineData(8000, "TextPrimaryBrush")]
    public void BattlesBrushKey_LowBattlesMarkedWarn(double battles, string expected)
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.5, battles));
        Assert.Equal(expected, vm.BattlesBrushKey);
    }

    [Fact]
    public void PlayerLineText_CombinesClanTagAndName()
    {
        var player = MakePlayer("Motivations", 0.5, 100);
        player.ClanTag = "ABC";
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal("[ABC] Motivations", vm.PlayerLineText);
    }

    [Fact]
    public void PlayerLineText_WithoutClanTag_ShowsNameOnly()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("SoloPlayer", 0.5, 100));
        Assert.Equal("SoloPlayer", vm.PlayerLineText);
    }

    [Theory]
    [InlineData(6, "VI TestShip")]
    [InlineData(7, "VII TestShip")]
    [InlineData(10, "X TestShip")]
    [InlineData(11, "XI TestShip")]
    public void ShipLineText_UsesRomanTier(int tier, string expected)
    {
        var player = MakePlayer("A", 0.5, 100, tier: tier);
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal(expected, vm.ShipLineText);
        Assert.Equal(tier == 11 ? "XI" : expected.Split(' ')[0], vm.TierRoman);
    }

    [Fact]
    public void WeightedWinrateText_Unknown_ShowsDash()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.5, 100));
        Assert.Equal("-", vm.WeightedWinrateText);
        Assert.Equal("HiddenBrush", vm.WeightedWinrateBrushKey);
    }

    [Fact]
    public void WeightedWinrateText_Known_FormatsPercent()
    {
        var player = MakePlayer("A", 0.5, 1000);
        player.WeightedWinrate = 0.4582;
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal("45.8%", vm.WeightedWinrateText);
    }

    [Theory]
    [InlineData(0.62, 1000, "UnicumBrush")]
    [InlineData(0.45, 1000, "ApeBrush")]
    [InlineData(0.52, 1000, "TextPrimaryBrush")]
    public void WeightedWinrateBrushKey_FollowsWinrateThresholds(
        double winrate, double battles, string expected)
    {
        var player = MakePlayer("A", 0.5, battles);
        player.WeightedWinrate = winrate;
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal(expected, vm.WeightedWinrateBrushKey);
    }

    [Theory]
    [InlineData(ShipClass.Carrier, 0)]
    [InlineData(ShipClass.Battleship, 1)]
    [InlineData(ShipClass.Cruiser, 2)]
    [InlineData(ShipClass.Destroyer, 3)]
    [InlineData(ShipClass.Submarine, 4)]
    [InlineData(ShipClass.Unknown, 5)]
    public void ShipClassSortKey_OrdersCarrierFirst(ShipClass shipClass, int expected)
    {
        Assert.Equal(expected, PlayerRowViewModel.ShipClassSortKey(shipClass));
    }
}
