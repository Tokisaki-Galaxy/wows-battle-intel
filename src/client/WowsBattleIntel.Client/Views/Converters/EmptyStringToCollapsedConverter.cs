using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WowsBattleIntel.Client.Views.Converters;

/// <summary>
/// 空字符串到折叠的可见性转换。空串或 null 折叠，非空可见。
/// </summary>
public sealed class EmptyStringToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
