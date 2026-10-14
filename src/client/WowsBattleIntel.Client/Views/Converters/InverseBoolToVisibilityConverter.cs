using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WowsBattleIntel.Client.Views.Converters;

/// <summary>
/// 布尔值取反后转成可见性。
/// </summary>
/// <remarks>
/// 与内置的 BooleanToVisibilityConverter 配套，一个控制内容显示，
/// 一个控制空态提示，避免两处绑定写互补的逻辑属性。
/// </remarks>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("可见性由视图模型单向提供，不支持反向转换。");
}
