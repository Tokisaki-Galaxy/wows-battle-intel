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

    /// <summary>单实例守卫。整个进程生命周期持有，退出时释放。</summary>
    private readonly Services.SingleInstanceGuard singleInstance = new();

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例判定放在一切昂贵初始化之前：重复启动时第二份不该跑配置
        // 加载与数据库打开，把既有窗口拉到前台后自己退出。
        if (!singleInstance.TryAcquire())
        {
            // 已有实例在跑：把它拉到前台，自己安静退出。
            SingleInstanceGuard.SignalExistingInstance();
            Shutdown(0);
            return;
        }

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

            // 把上次运行留下的日志回填进内存缓冲，界面日志页签能看到崩溃前的现场。
            Services.Logs.ReplaceHistory(Services.Logs.LoadRecent());

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

            Services.Logs.Info($"应用启动，日志目录 {Services.Logs.LogDirectory}");

            // StartupUri 已移除，窗口在这里手动创建，便于在创建前挂上
            // 消息钩子（单实例前台广播的接收端）。显式关闭模式下窗口关闭
            // 不自动退应用，这里补上：主窗口关了就该整份退出。
            var window = new MainWindow();

            // WPF 窗口句柄在 Show 之前不存在，先显式创建句柄再挂钩子，
            // 否则 HwndSource.FromHwnd(0) 返回空，广播接收端永远不会生效。
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            RegisterShowSelfHandler(window);

            MainWindow = window;
            window.Closed += (_, _) => Shutdown();
            window.Show();
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

    /// <summary>
    /// 挂上消息钩子，接收第二实例发来的"到前台"广播。
    /// </summary>
    private void RegisterShowSelfHandler(MainWindow window)
    {
        var source = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(window).Handle);
        source?.AddHook(HookShowSelf);
    }

    /// <summary>
    /// 收到自定义消息时还原窗口并带到前台。
    /// </summary>
    /// <remarks>
    /// 第二实例已通过 AllowSetForegroundWindow 让渡前台权限，但部分环境下
    /// Activate 仍被系统收紧。这里用 AttachThreadInput 把当前线程挂进前台
    /// 线程的输入队列再 SetForegroundWindow，这是绕开前台限制的标准手段；
    /// Topmost 一升一降作最后兜底。每次唤起都记日志，链路断在哪一环能直接看。
    /// </remarks>
    private nint HookShowSelf(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)msg == SingleInstanceGuard.WmShowSelf)
        {
            if (MainWindow is Views.MainWindow { } main)
            {
                Services.Logs.Info("收到单实例唤起广播，正在还原窗口");
                main.Show();
                if (main.WindowState == WindowState.Minimized)
                {
                    main.WindowState = WindowState.Normal;
                }

                NativeWin.RestoreAndActivate(
                    new System.Windows.Interop.WindowInteropHelper(main).Handle);
                main.Activate();
                main.Topmost = true;
                main.Topmost = false;
                handled = true;
                Services.Logs.Info("单实例唤起完成");
            }
        }

        return 0;
    }

    /// <summary>窗口前台操作的原生手段。集中在这一处，避免散落。</summary>
    private static class NativeWin
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(nint hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        /// <summary>把目标窗口抬到前台。挂进前台线程输入队列后再设前台。</summary>
        internal static void RestoreAndActivate(nint hwnd)
        {
            if (hwnd == 0)
            {
                return;
            }

            _ = ShowWindow(hwnd, 5); // SW_SHOW

            nint foreground = GetForegroundWindowSafe();
            _ = GetWindowThreadProcessId(foreground, out uint foregroundPid);
            uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
            uint selfThread = GetCurrentThreadId();

            bool attached = foregroundThread != 0 && foregroundThread != selfThread
                && AttachThreadInput(selfThread, foregroundThread, true);
            try
            {
                _ = SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                {
                    _ = AttachThreadInput(selfThread, foregroundThread, false);
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        private static nint GetForegroundWindowSafe() => GetForegroundWindow();
    }

    /// <summary>路径选择器，供配置界面使用。</summary>
    public static IPathPicker PathPicker { get; } = new WpfPathPicker();

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        Services?.Dispose();
        singleInstance.Dispose();
        base.OnExit(e);
    }
}
