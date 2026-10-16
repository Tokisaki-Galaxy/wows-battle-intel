using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Scoring;
using WowsBattleIntel.Client.Views;
using WowsBattleIntel.Stats;

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

        // Velopack 更新钩子必须最先执行，处理安装/更新/卸载回调。
        // 短钩子运行完即返回，不影响正常启动路径。
        Velopack.VelopackApp.Build()
            .SetLogger(new VelopackLogAdapter())
            .Run();

        // 赛前名单的舰种与等级回填：Core 不引用 Stats，注入委托桥接。
        ArenaInfoParser.ShipMetaLookup = shipId =>
        {
            if (!ShipCatalog.TryGetMeta(shipId, out var meta) || meta is null)
            {
                return null;
            }

            return (meta.Type, meta.Tier);
        };

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

            // 按配置应用主题，默认跟随系统。
            ThemeService.ApplyTheme(ThemeService.ParseMode(config.Theme));

            // 观测常驻：路径可用即开始。界面不再提供开始/停止开关，
            // 观测是应用的基本运行方式，由配置换绑负责重定向。
            if (config.IsUsable)
            {
                Services.StartObservation();
            }

            // 延迟后台检查新版本，节流与确认见 UpdateService。
            UpdateService.CheckOnStartup(config);
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

    /// <summary>路径选择器，供配置界面使用。</summary>
    public static IPathPicker PathPicker { get; } = new WpfPathPicker();

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        Services?.Dispose();
        base.OnExit(e);
    }
}
