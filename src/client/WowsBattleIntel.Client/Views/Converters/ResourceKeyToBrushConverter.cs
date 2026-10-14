using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WowsBattleIntel.Client.Views.Converters;

/// <summary>
/// 把主题资源的键名转成画刷，支持派生徽章三件套。
/// </summary>
/// <remarks>
/// 视图模型只决定"用哪种语义的颜色"，具体色值仍由主题资源决定。
/// 这样视图模型不直接持有 WPF 画刷，换主题时不用改逻辑。
/// parameter 为空时返回键名对应的实色画刷；
/// parameter 为 soft 时返回键名对应的柔底画刷，为 bd 时返回同族描边画刷。
/// </remarks>
public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
        {
            return Brushes.Transparent;
        }

        var suffix = parameter as string;
        var resolvedKey = suffix switch
        {
            "soft" => key.Replace("Brush", "SoftBrush", StringComparison.Ordinal),
            "bd" => key.Replace("Brush", "BdBrush", StringComparison.Ordinal),
            _ => key,
        };

        return Application.Current?.TryFindResource(resolvedKey) as Brush ?? Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("颜色键名由视图模型单向提供，不支持反向转换。");
}
