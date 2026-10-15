using System.Windows;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Client.ViewModels;
using WowsBattleIntel.Client.Views;
using ThemeMode = WowsBattleIntel.Client.Services.ThemeMode;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 界面加载冒烟测试。
/// </summary>
/// <remarks>
/// XAML 里引用了不存在的资源键或绑定路径写错时，编译不报错，只有窗口真正
/// 构造时才抛。这里在共享 WPF 线程上加载主题并实例化主窗口，把这类问题挡在
/// 提交之前。测试不显示窗口，只走构造与资源解析。
/// </remarks>
[Collection("WpfApplication")]
public class MainWindowSmokeTests
{
    [Fact]
    public void MainWindow_LoadsWithThemeResources()
    {
        WpfApplicationFixture.Run(_ =>
        {
            // 与生产同一路径装配主题资源，避免测试与真实启动漂移。
            ThemeService.ApplyTheme(ThemeMode.Dark);

            using var holder = TestServices.CreateAsync().GetAwaiter().GetResult();
            var viewModel = new MainViewModel(holder.Services);
            var window = new MainWindow(viewModel);

            // 触发一次布局，能暴露模板与资源解析的问题。
            window.Measure(new Size(1280, 800));
            window.Arrange(new Rect(0, 0, 1280, 800));
            window.UpdateLayout();

            window.Close();
        });
    }
}
