using System.Windows;
using System.Windows.Controls;
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
    private bool logAutoScrollEnabled = true;

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
        viewModel.Logs.EntriesAppended += OnLogEntriesAppended;
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

    /// <summary>
    /// 新日志到达时滚到底部。用户上滚后暂停自动滚动，重新滚到底再恢复。
    /// </summary>
    private void OnLogEntriesAppended()
    {
        var list = LogEntriesList;
        if (list is null)
        {
            return;
        }

        if (logAutoScrollEnabled && list.Items.Count > 0)
        {
            list.ScrollIntoView(list.Items[^1]);
        }
    }

    /// <summary>
    /// 日志列表滚动状态跟踪。内容高度减视口高度即底部偏移，剩余不足 1 像素
    /// 视为在底部。
    /// </summary>
    private void OnLogListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var viewer = e.OriginalSource as ScrollViewer;
        if (viewer is null)
        {
            return;
        }

        bool atBottom = viewer.VerticalOffset >= viewer.ScrollableHeight - 0.5;
        if (atBottom)
        {
            logAutoScrollEnabled = true;
        }
        else if (e.VerticalChange < 0)
        {
            // 只有用户主动向上滚才暂停。新条目入列把内容顶下去时偏移相对
            // 视图是向上的，此时不该误判为用户上滚。
            logAutoScrollEnabled = false;
        }
    }
}
