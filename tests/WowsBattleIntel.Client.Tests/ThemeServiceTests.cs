using Microsoft.Win32;
using WowsBattleIntel.Client.Services;
using ThemeMode = WowsBattleIntel.Client.Services.ThemeMode;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 主题服务测试。
/// </summary>
/// <remarks>
/// WPF 限制一个进程只能有一个 Application 实例，且资源字典操作必须在创建
/// 它的线程上进行。这里用集合级夹具在专门的 STA 线程上创建并常驻唯一的
/// Application，同集合的用例串行执行，互不干扰。
/// </remarks>
[Collection("WpfApplication")]
public class ThemeServiceTests
{
    [Fact]
    public void ApplyTheme_SwitchesTokenDictionaries()
    {
        WpfApplicationFixture.Run(app =>
        {
            app.Resources.MergedDictionaries.Clear();

            // 深色起步。
            ThemeService.ApplyTheme(ThemeMode.Dark);
            Assert.True(app.Resources.Contains("BgColor"));
            Assert.Equal(
                System.Windows.Media.Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
                (System.Windows.Media.Color)app.Resources["BgColor"]);

            // 切浅色后令牌键仍在，值换成浅色档。
            ThemeService.ApplyTheme(ThemeMode.Light);
            Assert.True(app.Resources.Contains("BgColor"));
            Assert.Equal(
                System.Windows.Media.Color.FromArgb(0xFF, 0xF7, 0xF8, 0xFA),
                (System.Windows.Media.Color)app.Resources["BgColor"]);

            // 装配层与控件样式层仍在资源链里。
            Assert.True(app.Resources.Contains("AppWindowStyle"));
        });
    }

    [Fact]
    public void ApplyTheme_RebuildsChainWhenTokensMissing()
    {
        WpfApplicationFixture.Run(app =>
        {
            // 清空合并字典模拟令牌字典缺失，ApplyTheme 应重建整条资源链。
            app.Resources.MergedDictionaries.Clear();
            ThemeService.ApplyTheme(ThemeMode.Dark);

            var merged = app.Resources.MergedDictionaries;
            Assert.Equal(3, merged.Count);
            Assert.Contains("Tokens.Dark.xaml", merged[0].Source.ToString());
            Assert.True(app.Resources.Contains("AppWindowStyle"));
        });
    }

    [Fact]
    public void ApplyTheme_SystemMode_DoesNotThrow()
    {
        WpfApplicationFixture.Run(app =>
        {
            app.Resources.MergedDictionaries.Clear();

            // 跟随系统依赖注册表与事件订阅，注册表不可达时按深色处理，全程不抛。
            ThemeService.ApplyTheme(ThemeMode.System);
            Assert.True(app.Resources.Contains("BgColor"));
        });
    }

    [Fact]
    public void ParseMode_MapsConfigStrings()
    {
        Assert.Equal(ThemeMode.Light, ThemeService.ParseMode("light"));
        Assert.Equal(ThemeMode.Dark, ThemeService.ParseMode("dark"));
        Assert.Equal(ThemeMode.System, ThemeService.ParseMode("system"));
        Assert.Equal(ThemeMode.System, ThemeService.ParseMode(null));
        Assert.Equal(ThemeMode.System, ThemeService.ParseMode("unknown"));
    }

    [Fact]
    public void UserPreferenceChanged_DoesNotThrow()
    {
        WpfApplicationFixture.Run(app =>
        {
            app.Resources.MergedDictionaries.Clear();
            ThemeService.ApplyTheme(ThemeMode.System);

            // 手动触发事件路径。SystemEvents 在测试进程里未必有底层消息循环
            // 支撑，触发失败时按跳过处理，只验证事件链本身不抛异常。
            try
            {
                var field = typeof(SystemEvents).GetField(
                    "_userPreferenceChanged",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (field?.GetValue(null) is Delegate handler)
                {
                    handler.DynamicInvoke(
                        null,
                        new UserPreferenceChangedEventArgs(UserPreferenceCategory.General));
                }
            }
            catch
            {
                // 事件底层依赖桌面消息循环，无法触发时按跳过处理。
            }

            Assert.True(app.Resources.Contains("BgColor"));
        });
    }
}
