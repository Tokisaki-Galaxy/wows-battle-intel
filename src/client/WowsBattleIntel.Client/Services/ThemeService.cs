using System.Windows;
using Microsoft.Win32;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 主题模式。
/// </summary>
public enum ThemeMode
{
    /// <summary>浅色主题。</summary>
    Light,

    /// <summary>深色主题。</summary>
    Dark,

    /// <summary>跟随系统设置。</summary>
    System,
}

/// <summary>
/// 主题服务。
/// </summary>
/// <remarks>
/// 负责把令牌字典换到目标主题，装配层与控件样式层保持不动。
/// 跟随系统模式读取注册表的 AppsUseLightTheme，并订阅 SystemEvents
/// 的 UserPreferenceChanged，系统切换深浅色时自动跟随。
/// 所有公开方法必须在 UI 线程调用。
/// </remarks>
public static class ThemeService
{
    private const string TokensDarkSuffix = "Tokens.Dark.xaml";
    private const string TokensLightSuffix = "Tokens.Light.xaml";
    private const string TokensDarkSource = "Resources/Theme/Tokens.Dark.xaml";
    private const string TokensLightSource = "Resources/Theme/Tokens.Light.xaml";
    private const string ThemeAssemblySource = "Resources/Theme.xaml";
    private const string ControlsSharedSource = "Resources/Theme/Controls.Shared.xaml";

    private static readonly Uri TokensDarkUri = BuildPackUri(TokensDarkSource);
    private static readonly Uri TokensLightUri = BuildPackUri(TokensLightSource);

    private static ThemeMode? currentMode;
    private static bool systemEventsHooked;

    /// <summary>
    /// 应用主题。UI 线程调用。
    /// </summary>
    /// <param name="mode">目标模式。</param>
    public static void ApplyTheme(ThemeMode mode)
    {
        if (Application.Current is null)
        {
            return;
        }

        ThemeMode effective = mode == ThemeMode.System ? ResolveSystemMode() : mode;
        ApplyEffectiveTheme(effective);
        currentMode = mode;

        // 跟随系统才需要挂事件；其它模式挂上也无妨，但只在 System 模式下响应。
        EnsureSystemEventsHooked();
    }

    /// <summary>
    /// 把配置字符串解析为主题模式。空值或未知值按跟随系统处理。
    /// </summary>
    public static ThemeMode ParseMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

    /// <summary>
    /// 读取系统当前应用主题。注册表缺失或读取失败时按深色处理。
    /// </summary>
    private static ThemeMode ResolveSystemMode()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);

            if (value is int light && light != 0)
            {
                return ThemeMode.Light;
            }
        }
        catch
        {
            // 注册表不可达时退回深色，与历史默认一致。
        }

        return ThemeMode.Dark;
    }

    /// <summary>
    /// 挂接系统偏好变更事件。事件在线程池线程触发，必须封送回 UI 线程。
    /// </summary>
    private static void EnsureSystemEventsHooked()
    {
        if (systemEventsHooked)
        {
            return;
        }

        systemEventsHooked = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || currentMode != ThemeMode.System)
        {
            return;
        }

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        // SystemEvents 在线程池线程上触发，封送回 UI 线程再动资源。
        app.Dispatcher.BeginInvoke(() =>
        {
            if (currentMode == ThemeMode.System)
            {
                ApplyEffectiveTheme(ResolveSystemMode());
            }
        });
    }

    /// <summary>
    /// 替换令牌字典。能定位到旧令牌字典就原位替换，找不到就重建整条资源链。
    /// </summary>
    private static void ApplyEffectiveTheme(ThemeMode effective)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        Uri targetUri = effective == ThemeMode.Light ? TokensLightUri : TokensDarkUri;
        var merged = app.Resources.MergedDictionaries;

        int oldIndex = -1;
        for (int i = 0; i < merged.Count; i++)
        {
            string? source = merged[i].Source?.ToString();
            if (source is not null
                && (source.EndsWith(TokensDarkSuffix, StringComparison.OrdinalIgnoreCase)
                    || source.EndsWith(TokensLightSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                oldIndex = i;
                break;
            }
        }

        if (oldIndex >= 0)
        {
            // 令牌字典必须在装配层 Theme.xaml 之前，原位替换可保证次序不变。
            merged[oldIndex] = new ResourceDictionary { Source = targetUri };
            return;
        }

        RebuildResourceChain(targetUri);
    }

    /// <summary>
    /// 找不到令牌字典时重建整条资源链：令牌、装配层、控件样式。
    /// </summary>
    private static void RebuildResourceChain(Uri tokensUri)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var merged = app.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(new ResourceDictionary { Source = tokensUri });
        merged.Add(new ResourceDictionary { Source = BuildPackUri(ThemeAssemblySource) });
        merged.Add(new ResourceDictionary { Source = BuildPackUri(ControlsSharedSource) });
    }

    private static Uri BuildPackUri(string relativeSource)
    {
        string assemblyName = typeof(ThemeService).Assembly.GetName().Name
            ?? "WowsBattleIntel.Client";
        return new Uri(
            $"pack://application:,,,/{assemblyName};component/{relativeSource}",
            UriKind.Absolute);
    }
}
