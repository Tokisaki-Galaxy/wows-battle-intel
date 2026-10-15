using System.Globalization;
using System.Windows.Data;
using WowsBattleIntel.Client.ViewModels;

namespace WowsBattleIntel.Client.Views.Converters;

/// <summary>
/// 把玩家行转成整行底色画刷的资源键，经 <see cref="ResourceKeyToBrushConverter"/> 换成画刷。
/// </summary>
/// <remarks>
/// 行背景按水平分档渲染：海王紫底、猴子红底，普通与未知透明。
/// 舰种信息由玩家列前缀承担，行色不再按舰种分块。
/// </remarks>
public sealed class RowBackgroundBrushKeyConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is PlayerRowViewModel row
            ? row.TierBrushKey switch
            {
                "UnicumBrush" => "RowUnicumBrush",
                "ApeBrush" => "RowApeBrush",
                _ => "TransparentBrush",
            }
            : "TransparentBrush";
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
