using System.Windows;
using WowsBattleIntel.Client.ViewModels;
using WowsBattleIntel.Client.Views;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 界面加载冒烟测试。
/// </summary>
/// <remarks>
/// XAML 里引用了不存在的资源键或绑定路径写错时，编译不报错，只有窗口真正
/// 构造时才抛。这里在 STA 线程上加载主题并实例化主窗口，把这类问题挡在
/// 提交之前。测试不显示窗口，只走构造与资源解析。
/// </remarks>
public class MainWindowSmokeTests
{
    [Fact]
    public void MainWindow_LoadsWithThemeResources()
    {
        Exception? failure = null;

        // WPF 控件必须在 STA 线程上创建。
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new Application();
                    app.Resources.MergedDictionaries.Add(
                        new ResourceDictionary
                        {
                            Source = new Uri(
                                "pack://application:,,,/WowsBattleIntel.Client;component/Resources/Theme/Controls.Dark.xaml",
                                UriKind.Absolute),
                        });
                }

                using var holder = TestServices.CreateAsync().GetAwaiter().GetResult();
                var viewModel = new MainViewModel(holder.Services);
                var window = new MainWindow(viewModel);

                // 触发一次布局，能暴露模板与资源解析的问题。
                window.Measure(new Size(1280, 800));
                window.Arrange(new Rect(0, 0, 1280, 800));
                window.UpdateLayout();

                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "界面加载超时");
        Assert.Null(failure);
    }
}
