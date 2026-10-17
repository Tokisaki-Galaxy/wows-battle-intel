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

    /// <summary>运行日志页签在主标签页里的下标。</summary>
    private const int LogTabIndex = 1;

    /// <summary>运行日志列表，供测试在页签实例化后取到控件。</summary>
    internal System.Windows.Controls.ListBox LogEntriesListForTest => LogEntriesList;

    /// <summary>首次加载完成前忽略页签切换，避免与 OnLoaded 的首次加载重复。</summary>
    private bool initialLoadCompleted;

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

        initialLoadCompleted = true;
    }

    /// <summary>
    /// 主标签页切换。切回对局情报页时按最新后台状态重刷一次详情。
    /// </summary>
    /// <remarks>
    /// 切页期间赛前收敛循环照常在后台跑，界面被盖住时用户看不到变化。
    /// 切回来重刷一次，名单与倒计时对齐到最新进度，不停在旧文字上。
    /// 首次加载完成前不处理，交给 OnLoaded 走一次即可。
    /// </remarks>
    private async void OnMainTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 事件会从内部控件冒泡上来，只处理主标签页自身的选择变化。
        if (!ReferenceEquals(e.OriginalSource, MainTabs))
        {
            return;
        }

        if (MainTabs.SelectedIndex == LogTabIndex)
        {
            // 切到运行日志时直接停在最新的那一条，而不是上次停留的位置。
            logAutoScrollEnabled = true;

            // 页签的内容模板在切换后的布局阶段才实例化，此刻列表可能还不存在，
            // 因此延后一帧再滚。延迟到 Loaded 之后，不能与同优先级的布局任务
            // 抢先后，否则可能先于内容生成执行。
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() =>
                {
                    ScrollLogsToBottom();
                    LogEntriesList?.UpdateLayout();
                    ScrollLogsToBottom();
                }));
            return;
        }

        if (!initialLoadCompleted)
        {
            return;
        }

        if (MainTabs.SelectedIndex == 0)
        {
            await viewModel.RefreshIntelViewAsync().ConfigureAwait(true);
        }
    }

    private async void OnBattleListChanged()
    {
        await viewModel.Calibration.LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 新日志到达时滚到底部。用户上滚后暂停自动滚动，重新滚到底再恢复。
    /// </summary>
    /// <remarks>
    /// 新条目刚入列时容器还没生成，直接 ScrollIntoView 有时停在倒数第二条。
    /// 推迟到布局完成后再滚一次最稳妥。
    /// </remarks>
    private void OnLogEntriesAppended()
    {
        if (!logAutoScrollEnabled)
        {
            return;
        }

        ScrollLogsToBottom();
    }

    /// <summary>
    /// 在当前界面上滚到日志末尾。
    /// </summary>
    /// <remarks>
    /// 窗口尚未显示或列表为空时直接跳过，避免无谓的调度。
    /// </remarks>
    private void ScrollLogsToBottom()
    {
        var list = LogEntriesList;
        if (list is null || list.Items.Count == 0)
        {
            return;
        }

        object lastItem = list.Items[^1];

        // 立即滚一次，保证响应及时。
        list.ScrollIntoView(lastItem);

        // 再在布局完成后补一次，覆盖新容器尚未生成、滚动距离未定稿的情况。
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (LogEntriesList is { Items.Count: > 0 } current)
                {
                    current.ScrollIntoView(current.Items[^1]);
                }
            }));
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
