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
        // 无常用等级数据时不带前缀。
        Assert.Equal("61,700", vm.AvgDamageText);
    }

    [Fact]
    public void AvgDamageText_WithFavoriteTier_ShowsPrefix()
    {
        var player = MakePlayer("A", 0.5, 100);
        player.AccountAvgDamage = 61700;
        player.FavoriteTier = 10;
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal("(X) 61,700", vm.AvgDamageText);
    }

    [Fact]
    public void ClassStats_Unknown_ShowsDashes()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.5, 100));
        Assert.Equal("-", vm.ClassBattlesText);
        Assert.Equal("-", vm.ClassWinrateText);
        Assert.Equal("-", vm.ClassDamageText);
    }

    [Fact]
    public void ClassStats_Known_ShowsBattlesWinrateAndDamage()
    {
        var player = MakePlayer("A", 0.5, 100);
        player.ClassBattles = 3412;
        player.ClassWins = 1938;
        player.ClassDamage = 231_000_000;
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal("3,412", vm.ClassBattlesText);
        Assert.Equal("56.8%", vm.ClassWinrateText);
        Assert.Equal("67,702", vm.ClassDamageText);
    }

    [Fact]
    public void ClassMasteryBrushKey_FollowsWinrateThresholds()
    {
        var player = MakePlayer("A", 0.5, 1000);
        player.ClassBattles = 2000;
        player.ClassWins = 1240;
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal("UnicumBrush", vm.ClassMasteryBrushKey);
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

    [Fact]
    public void ClanTagText_WrapsTagInBrackets()
    {
        var player = MakePlayer("A", 0.5, 100);
        player.ClanTag = "ABC";
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal("[ABC]", vm.ClanTagText);
        Assert.True(vm.HasClanTag);
        Assert.Equal("A", vm.PlayerNameText);
    }

    [Fact]
    public void ClanTagText_KeepsExistingBrackets()
    {
        var player = MakePlayer("A", 0.5, 100);
        player.ClanTag = "[ABC]";
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal("[ABC]", vm.ClanTagText);
    }

    [Fact]
    public void ClanTagText_EmptyTag_HasNoMark()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("SoloPlayer", 0.5, 100));
        Assert.Equal(string.Empty, vm.ClanTagText);
        Assert.False(vm.HasClanTag);
        Assert.Equal(string.Empty, vm.ClanTagBrushKey);
        Assert.Equal(string.Empty, vm.ClanToolTip);
    }

    [Fact]
    public void ClanTagBrushKey_MapsClanColorKey()
    {
        var player = MakePlayer("A", 0.5, 100);
        player.ClanTag = "OMNI";
        player.ClanColorKey = "UnicumBrush";
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal("UnicumBrush", vm.ClanTagBrushKey);
        Assert.Contains("紫会", vm.ClanToolTip);
    }

    [Fact]
    public void SetClanDetail_AppendsToolTipDetail()
    {
        var player = MakePlayer("A", 0.5, 100);
        player.ClanTag = "OMNI";
        var vm = PlayerRowViewModel.FromPlayer(player);
        vm.SetClanDetail("Omniscient | 30 名成员 | 聚合胜率 56.0%");
        Assert.Contains("Omniscient", vm.ClanToolTip);
        Assert.Contains("30 名成员", vm.ClanToolTip);
        Assert.Contains("56.0%", vm.ClanToolTip);
    }

    [Fact]
    public void SetClanDetail_SameDetail_NoChange()
    {
        var player = MakePlayer("A", 0.5, 100);
        player.ClanTag = "OMNI";
        var vm = PlayerRowViewModel.FromPlayer(player);
        vm.SetClanDetail("detail");
        vm.SetClanDetail("detail");
        Assert.Equal("[OMNI]\ndetail", vm.ClanToolTip);
    }

    [Theory]
    [InlineData(6, "BB VI TestShip")]
    [InlineData(7, "BB VII TestShip")]
    [InlineData(10, "BB X TestShip")]
    [InlineData(11, "BB XI TestShip")]
    public void ShipLineText_UsesRomanTier(int tier, string expected)
    {
        var player = MakePlayer("A", 0.5, 100, tier: tier);
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal(expected, vm.ShipLineText);
        Assert.Equal(tier == 11 ? "XI" : expected.Split(' ')[1], vm.TierRoman);
    }

    [Theory]
    [InlineData(ShipClass.Carrier, "CV X TestShip")]
    [InlineData(ShipClass.Battleship, "BB X TestShip")]
    [InlineData(ShipClass.Cruiser, "CA X TestShip")]
    [InlineData(ShipClass.Destroyer, "DD X TestShip")]
    [InlineData(ShipClass.Submarine, "SS X TestShip")]
    public void ShipLineText_PrefixesShipClass(ShipClass shipClass, string expected)
    {
        var player = MakePlayer("A", 0.5, 100, shipClass: shipClass);
        var vm = PlayerRowViewModel.FromPlayer(player);
        Assert.Equal(expected, vm.ShipLineText);
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

    [Fact]
    public void RefreshFrom_UpdatesPlaceholderRowWithStats()
    {
        // 阶段一：快照缺失，行显示占位符。
        var placeholder = MakePlayer("LateLoader", -1, -1);
        placeholder.AccountId = "2002";
        placeholder.ShipWinrate = -1;
        placeholder.ShipBattles = -1;
        var vm = PlayerRowViewModel.FromPlayer(placeholder);

        Assert.Equal("-", vm.BattlesText);
        Assert.Equal("-", vm.WeightedWinrateText);
        Assert.Equal("-", vm.ShipWinrateText);
        Assert.Equal("隐藏", vm.TierLabel);
        Assert.Equal("HiddenBrush", vm.WinrateBrushKey);
        Assert.Equal("HiddenBrush", vm.WeightedWinrateBrushKey);

        // 阶段二：补拉落地，同一行对象原地刷新为真实统计。
        var fetched = MakePlayer("LateLoader", 0.62, 5000);
        fetched.AccountId = "2002";
        fetched.AccountAvgDamage = 95000;
        fetched.FavoriteTier = 10;
        fetched.WeightedWinrate = 0.55;
        fetched.ShipBattles = 300;
        fetched.ShipWinrate = 0.64;
        fetched.ShipAvgDamage = 80000;
        fetched.ClassBattles = 2100;
        fetched.ClassWins = 1302;

        vm.RefreshFrom(fetched);

        Assert.Equal("5,000", vm.BattlesText);
        Assert.Equal("62.0%", vm.WinrateText);
        Assert.Equal("55.0%", vm.WeightedWinrateText);
        Assert.Equal("海王", vm.TierLabel);
        Assert.Equal("(X) 95,000", vm.AvgDamageText);
        Assert.Equal("UnicumBrush", vm.WinrateBrushKey);
        // 加权 55% 在中段，保持主文本色。
        Assert.Equal("TextPrimaryBrush", vm.WeightedWinrateBrushKey);
        Assert.Equal("300", vm.ShipBattlesText);
        Assert.Equal("64.0%", vm.ShipWinrateText);
        Assert.Equal("80,000", vm.ShipDamageText);
        Assert.Equal("2,100", vm.ClassBattlesText);
        Assert.Equal("62.0%", vm.ClassWinrateText);
    }

    [Fact]
    public void RefreshFrom_RaisesChangeNotificationsForDerivedProperties()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", -1, -1));

        var notified = new List<string>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);

        var fetched = MakePlayer("A", 0.62, 5000);
        fetched.WeightedWinrate = 0.55;
        vm.RefreshFrom(fetched);

        // 渐进式更新依赖属性通知驱动界面刷新，关键列都必须有通知。
        Assert.Contains(nameof(PlayerRowViewModel.WinrateText), notified);
        Assert.Contains(nameof(PlayerRowViewModel.BattlesText), notified);
        Assert.Contains(nameof(PlayerRowViewModel.WeightedWinrateText), notified);
        Assert.Contains(nameof(PlayerRowViewModel.TierLabel), notified);
        Assert.Contains(nameof(PlayerRowViewModel.WinrateBrushKey), notified);
        Assert.Contains(nameof(PlayerRowViewModel.ShipWinrateText), notified);
    }

    [Fact]
    public void RefreshFrom_NullRecord_Throws()
    {
        var vm = PlayerRowViewModel.FromPlayer(MakePlayer("A", 0.5, 100));
        Assert.Throws<ArgumentNullException>(() => vm.RefreshFrom(null!));
    }
}
