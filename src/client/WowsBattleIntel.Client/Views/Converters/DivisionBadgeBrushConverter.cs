using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WowsBattleIntel.Client.Views.Converters;

/// <summary>
/// 组队编号徽章画刷。同一小队的玩家共享同一种颜色，单排透明。
/// </summary>
/// <remarks>
/// ApeRadar 在玩家名右上角用同色上标标出小队成员。绑定 DivisionSize 使用，
/// 输入为组队人数，1 表示单排返回透明，2 到 8 各给一族主题色，超出回退透明。
/// </remarks>
public sealed class DivisionBadgeBrushConverter : IValueConverter
{
    private static readonly Brush[] BadgeBrushes =
    [
        new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)),
        new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
        new SolidColorBrush(Color.FromRgb(0xAE, 0x94, 0xFA)),
        new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84)),
        new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65)),
        new SolidColorBrush(Color.FromRgb(0xF0, 0x62, 0x92)),
        new SolidColorBrush(Color.FromRgb(0x4D, 0xB6, 0xAC)),
    ];

    static DivisionBadgeBrushConverter()
    {
        foreach (var brush in BadgeBrushes)
        {
            brush.Freeze();
        }
    }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int size && size >= 2 && size - 2 < BadgeBrushes.Length)
        {
            return BadgeBrushes[size - 2];
        }

        return Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
