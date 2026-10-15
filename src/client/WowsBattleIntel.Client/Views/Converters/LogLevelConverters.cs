using System.Globalization;
using System.Windows.Data;
using WowsBattleIntel.Client.Services;

namespace WowsBattleIntel.Client.Views.Converters;

/// <summary>
/// 日志级别到中文短标签的转换。
/// </summary>
public sealed class LogLevelTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LogLevel level ? LogLevelLabels.Text(level) : string.Empty;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("级别标签由级别单向推导，不支持反向转换。");
}

/// <summary>
/// 日志级别到主题画刷键名的转换。
/// </summary>
/// <remarks>
/// Info 走次级文本色，Warn 走警告色，Error 走危险色。只给键名，具体色值
/// 由主题令牌决定，换主题不用改逻辑。
/// </remarks>
public sealed class LogLevelBrushKeyConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LogLevel level ? LogLevelLabels.BrushKey(level) : "TextSecondaryBrush";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("颜色键名由级别单向推导，不支持反向转换。");
}

/// <summary>
/// 日志级别的显示口径，标签与画刷键集中一处。
/// </summary>
internal static class LogLevelLabels
{
    /// <summary>级别中文短标签。</summary>
    public static string Text(LogLevel level) => level switch
    {
        LogLevel.Info => "信息",
        LogLevel.Warn => "警告",
        LogLevel.Error => "错误",
        _ => "信息",
    };

    /// <summary>级别对应的主题画刷键名。</summary>
    public static string BrushKey(LogLevel level) => level switch
    {
        LogLevel.Warn => "WarnBrush",
        LogLevel.Error => "DangerBrush",
        _ => "TextSecondaryBrush",
    };
}
