using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Client.Services;

namespace WowsBattleIntel.Client;

/// <summary>
/// 应用入口。
/// </summary>
/// <remarks>
/// 负责加载配置、初始化本地库、装配依赖，然后交给主窗口。
/// 依赖装配保持显式，不用容器，项目规模无需引入。
/// </remarks>
public partial class App : Application
{
    /// <summary>应用级服务集合，供各视图取用。</summary>
    public static AppServices Services { get; private set; } = null!;

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 界面用简体中文习惯的格式。
        var culture = new CultureInfo("zh-CN");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        try
        {
            var config = AppConfig.Load();
            Services = await AppServices.CreateAsync(config).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"初始化失败:\n{ex.Message}",
                "wows-battle-intel",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        Services?.Dispose();
        base.OnExit(e);
    }
}
