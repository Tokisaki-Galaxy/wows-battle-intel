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
            var viewModel = new MainViewModel(holder.Services, null, ReplayBackfillTests.CreateEmptyBackfill(holder.Services));
            var window = new MainWindow(viewModel);

            // 触发一次布局，能暴露模板与资源解析的问题。
            window.Measure(new Size(1280, 800));
            window.Arrange(new Rect(0, 0, 1280, 800));
            window.UpdateLayout();

            window.Close();
        });
    }

    [Fact]
    public void MainWindow_TabSwitchBackToIntel_DoesNotThrow()
    {
        WpfApplicationFixture.Run(_ =>
        {
            ThemeService.ApplyTheme(ThemeMode.Dark);

            using var holder = TestServices.CreateAsync().GetAwaiter().GetResult();
            var viewModel = new MainViewModel(holder.Services, null, ReplayBackfillTests.CreateEmptyBackfill(holder.Services));
            var window = new MainWindow(viewModel);
            window.Measure(new Size(1280, 800));
            window.Arrange(new Rect(0, 0, 1280, 800));
            window.UpdateLayout();

            // 切到运行日志再切回对局情报，回切会触发一次详情重刷，不得抛异常。
            var tabs = (System.Windows.Controls.TabControl)window.FindName("MainTabs")!;
            tabs.SelectedIndex = 1;
            window.UpdateLayout();
            tabs.SelectedIndex = 0;
            window.UpdateLayout();

            Assert.Equal(0, tabs.SelectedIndex);
            window.Close();
        });
    }

    [Fact]
    public void MainWindow_SwitchToLogTab_DoesNotThrowWithManyEntries()
    {
        WpfApplicationFixture.Run(_ =>
        {
            ThemeService.ApplyTheme(ThemeMode.Dark);

            using var holder = TestServices.CreateAsync().GetAwaiter().GetResult();
            var viewModel = new MainViewModel(holder.Services, null, ReplayBackfillTests.CreateEmptyBackfill(holder.Services));
            var window = new MainWindow(viewModel);
            window.Measure(new Size(1280, 800));
            window.Arrange(new Rect(0, 0, 1280, 800));
            window.UpdateLayout();

            // 攒够一屏以上的日志，切页时会实例化日志页签并触发一次滚底。
            for (int i = 0; i < 200; i++)
            {
                holder.Services.Logs.Info($"滚动验证 {i}");
            }

            var tabs = (System.Windows.Controls.TabControl)window.FindName("MainTabs")!;
            tabs.SelectedIndex = 1;
            window.UpdateLayout();

            // 切换后的自动滚动是异步的，这里同步推进一次，确认调度本身不出错。
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                new Action(() => { }),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            Assert.Equal(1, tabs.SelectedIndex);
            window.Close();
        });
    }
}
