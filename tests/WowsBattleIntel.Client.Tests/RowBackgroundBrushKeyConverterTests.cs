using System.Windows.Media;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Client.ViewModels;
using WowsBattleIntel.Client.Views.Converters;
using WowsBattleIntel.Core.Models;
using ThemeMode = WowsBattleIntel.Client.Services.ThemeMode;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 行背景转换器测试。
/// </summary>
/// <remarks>
/// 行背景绑定的属性是 Brush，转换器必须返回画刷对象而非资源键名。
/// 曾因为返回字符串导致绑定静默失败，行色不显示，这里把该契约固化下来。
/// </remarks>
[Collection("WpfApplication")]
public class RowBackgroundBrushKeyConverterTests
{
    private static PlayerRowViewModel MakeRow(double winrate, double battles, bool hidden = false)
    {
        return PlayerRowViewModel.FromPlayer(new PlayerRecord
        {
            AccountId = "1",
            PlayerName = "Tester",
            ShipName = "TestShip",
            ShipId = "TEST001",
            ShipClass = ShipClass.Battleship,
            ShipTier = 10,
            AccountWinrate = hidden ? -1 : winrate,
            AccountBattles = hidden ? -1 : battles,
            IsHidden = hidden,
        });
    }

    [Fact]
    public void Convert_ReturnsBrushNotResourceKey()
    {
        WpfApplicationFixture.Run(app =>
        {
            ThemeService.ApplyTheme(ThemeMode.Dark);
            var converter = new RowBackgroundBrushKeyConverter();

            var result = converter.Convert(
                MakeRow(0.62, 1000), typeof(Brush), null!, System.Globalization.CultureInfo.InvariantCulture);

            // 必须是可以直接赋给 Background 的画刷，而非字符串键名。
            Assert.IsAssignableFrom<Brush>(result);
        });
    }

    [Fact]
    public void Convert_UnicumPlayer_UsesUnicumRowBrush()
    {
        WpfApplicationFixture.Run(app =>
        {
            ThemeService.ApplyTheme(ThemeMode.Dark);
            var converter = new RowBackgroundBrushKeyConverter();

            var result = (Brush)converter.Convert(
                MakeRow(0.62, 1000), typeof(Brush), null!, System.Globalization.CultureInfo.InvariantCulture);

            var expected = (Brush)app.Resources["RowUnicumBrush"];
            Assert.Equal(expected.ToString(), result.ToString());
        });
    }

    [Fact]
    public void Convert_ApePlayer_UsesApeRowBrush()
    {
        WpfApplicationFixture.Run(app =>
        {
            ThemeService.ApplyTheme(ThemeMode.Dark);
            var converter = new RowBackgroundBrushKeyConverter();

            var result = (Brush)converter.Convert(
                MakeRow(0.45, 1000), typeof(Brush), null!, System.Globalization.CultureInfo.InvariantCulture);

            var expected = (Brush)app.Resources["RowApeBrush"];
            Assert.Equal(expected.ToString(), result.ToString());
        });
    }

    [Fact]
    public void Convert_NormalPlayer_Transparent()
    {
        WpfApplicationFixture.Run(app =>
        {
            ThemeService.ApplyTheme(ThemeMode.Dark);
            var converter = new RowBackgroundBrushKeyConverter();

            var result = (Brush)converter.Convert(
                MakeRow(0.52, 1000), typeof(Brush), null!, System.Globalization.CultureInfo.InvariantCulture);

            Assert.Equal(Brushes.Transparent.ToString(), result.ToString());
        });
    }

    [Fact]
    public void Convert_HiddenPlayer_Transparent()
    {
        WpfApplicationFixture.Run(app =>
        {
            ThemeService.ApplyTheme(ThemeMode.Dark);
            var converter = new RowBackgroundBrushKeyConverter();

            var result = (Brush)converter.Convert(
                MakeRow(0, 0, hidden: true), typeof(Brush), null!, System.Globalization.CultureInfo.InvariantCulture);

            // 隐藏战绩没有胜率，不该被判成猴子标红。
            Assert.Equal(Brushes.Transparent.ToString(), result.ToString());
        });
    }
}
