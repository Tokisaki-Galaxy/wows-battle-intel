using System.Windows;
using System.Windows.Threading;
using WowsBattleIntel.Client.ViewModels;

namespace WowsBattleIntel.Client.Views;

/// <summary>
/// 主窗口。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private DispatcherTimer? pendingTimer;

    /// <summary>
    /// 构造主窗口。
    /// </summary>
    public MainWindow()
        : this(new MainViewModel(App.Services, App.PathPicker))
    {
    }

    /// <summary>
    /// 用指定视图模型构造主窗口，供测试直接驱动界面而不依赖应用启动流程。
    /// </summary>
    internal MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        this.viewModel = viewModel;
        DataContext = viewModel;

        // 新回放入库后校准数据会变，顺手刷新，用户不用手动点。
        viewModel.BattleListChanged += OnBattleListChanged;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 首次加载对局列表与校准报告。
        await viewModel.LoadRecentBattlesAsync().ConfigureAwait(true);
        await viewModel.Calibration.LoadAsync().ConfigureAwait(true);

        // 待处理数量由后台协程推进，界面没有事件可挂，靠定时拉取。
        pendingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        pendingTimer.Tick += (_, _) => viewModel.RefreshObservationStatus();
        pendingTimer.Start();
    }

    private async void OnBattleListChanged()
    {
        await viewModel.Calibration.LoadAsync().ConfigureAwait(true);
    }
}
