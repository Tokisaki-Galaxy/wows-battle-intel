using System.Globalization;
using System.Windows;
using System.Windows.Data;
using WowsBattleIntel.Client.ViewModels;

namespace WowsBattleIntel.Client.Views.Converters;

/// <summary>
/// 覆盖层状态到可见性的转换。Hidden 返回 Collapsed，Loading 与 Error 都显示
/// 覆盖层本体；区分 Loading 与 Error 的内部元素用 LoadingStateToVisibilityConverter。
/// </summary>
public sealed class OverlayStateToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DetailOverlayState state && state != DetailOverlayState.Hidden
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 覆盖层状态到可见性的转换，仅 Loading 态可见。转圈动画用，Error 态
/// 不显示转圈换显示提示图标。
/// </summary>
public sealed class LoadingStateToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DetailOverlayState state && state == DetailOverlayState.Loading
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
