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

        viewModel = new MainViewModel(App.Services);
        DataContext = viewModel;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 首次加载对局列表。
        await viewModel.LoadRecentBattlesAsync().ConfigureAwait(true);
    }
}
