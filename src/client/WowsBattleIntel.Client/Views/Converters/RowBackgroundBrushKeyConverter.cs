using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WowsBattleIntel.Client.ViewModels;

namespace WowsBattleIntel.Client.Views.Converters;

/// <summary>
/// 把玩家行转成整行底色画刷。
/// </summary>
/// <remarks>
/// 行背景由用户配置的染色规则决定：规则命中哪条就用哪条的底色，都不命中透明。
/// 规则键名到画刷的解析在本转换器内完成。
///
/// 这里直接返回 Brush 而非资源键名：Background 是画刷属性，绑定到字符串
/// 时 WPF 找不到可用的类型转换会静默回退，属性保持默认值，行色不显示。
/// </remarks>
public sealed class RowBackgroundBrushKeyConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is PlayerRowViewModel row && row.RowBackgroundBrushKey.Length > 0
            ? row.RowBackgroundBrushKey
            : "TransparentBrush";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
