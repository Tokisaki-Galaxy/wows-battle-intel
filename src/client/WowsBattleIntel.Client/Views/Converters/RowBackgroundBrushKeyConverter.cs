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
/// 行背景按水平分档渲染：海王紫底、猴子红底，普通与未知透明。
/// 舰种信息由玩家列前缀承担，行色不再按舰种分块。
///
/// 这里直接返回 Brush 而非资源键名：Background 是画刷属性，绑定到字符串
/// 时 WPF 找不到可用的类型转换会静默回退，属性保持默认值，行色不显示。
/// 键名到画刷的解析在本转换器内完成。
/// </remarks>
public sealed class RowBackgroundBrushKeyConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is PlayerRowViewModel row
            ? row.TierBrushKey switch
            {
                "UnicumBrush" => "RowUnicumBrush",
                "ApeBrush" => "RowApeBrush",
                _ => "TransparentBrush",
            }
            : "TransparentBrush";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
