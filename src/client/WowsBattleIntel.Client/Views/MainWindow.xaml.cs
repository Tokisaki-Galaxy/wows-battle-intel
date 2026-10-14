using System.Windows;
using WowsBattleIntel.Client.ViewModels;

namespace WowsBattleIntel.Client.Views;

/// <summary>
/// 主窗口。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;

    /// <summary>
    /// 构造主窗口。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        viewModel = new MainViewModel(App.Services, App.PathPicker);
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
    }

    private async void OnBattleListChanged()
    {
        await viewModel.Calibration.LoadAsync().ConfigureAwait(true);
    }
}
