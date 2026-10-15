using System.Globalization;
using System.Windows.Data;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Views.Converters;

/// <summary>
/// 把舰种转成行底色画刷的资源键，经 <see cref="ResourceKeyToBrushConverter"/> 换成画刷。
/// </summary>
/// <remarks>
/// ApeRadar 按舰种给整行一个低透明底色，航母红、战列绿、巡洋蓝、驱逐橙、潜艇青。
/// 视图模型只暴露枚举，具体键名与色值都留在主题层，换主题时不用改逻辑。
/// 绑定 PlayerRowViewModel.ShipClass 使用。
/// </remarks>
public sealed class ShipClassToBrushKeyConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is ShipClass shipClass
            ? shipClass switch
            {
                ShipClass.Carrier => "RowCvBrush",
                ShipClass.Battleship => "RowBbBrush",
                ShipClass.Cruiser => "RowCaBrush",
                ShipClass.Destroyer => "RowDdBrush",
                ShipClass.Submarine => "RowSsBrush",
                _ => "TransparentBrush",
            }
            : "TransparentBrush";
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
