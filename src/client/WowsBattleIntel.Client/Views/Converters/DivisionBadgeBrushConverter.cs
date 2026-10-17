using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WowsBattleIntel.Client.Views.Converters;

/// <summary>
/// 组队编号徽章画刷。同一小队的玩家共享同一种颜色，单排透明。
/// </summary>
/// <remarks>
/// 绑定 <c>DivisionColorIndex</c>：由视图模型在整场比赛范围内统一分配，
/// 同一小队拿到同一个下标，不同小队保证拿到不同下标。-1 表示单排或
/// 赛前未知，返回透明。
///
/// 历史问题：早期按 <c>DivisionSize</c> 取色，同一局里两个双人队人数相同，
/// 会取到同一个颜色，界面上出现 4 个黄块分属两个不相干小队的错乱。
///
/// 为什么不在转换器里做分配：转换器是全局单例资源，WPF 按行的求值顺序
/// 不可控，且滚动虚拟化会重复求值，状态难以维护。分配逻辑放在视图模型里，
/// 一行代码就能保证跨行一致。
/// </remarks>
public sealed class DivisionBadgeBrushConverter : IValueConverter
{
    /// <summary>配色池。数量应不小于一局内可能出现的小队数上限。</summary>
    internal static readonly Brush[] BadgeBrushes =
    [
        new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)),
        new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
        new SolidColorBrush(Color.FromRgb(0xAE, 0x94, 0xFA)),
        new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84)),
        new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65)),
        new SolidColorBrush(Color.FromRgb(0xF0, 0x62, 0x92)),
        new SolidColorBrush(Color.FromRgb(0x4D, 0xB6, 0xAC)),
        new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00)),
        new SolidColorBrush(Color.FromRgb(0x8B, 0xA1, 0xFF)),
        new SolidColorBrush(Color.FromRgb(0xC5, 0xE1, 0xA5)),
        new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)),
        new SolidColorBrush(Color.FromRgb(0x9B, 0xD4, 0xF0)),
    ];

    static DivisionBadgeBrushConverter()
    {
        foreach (var brush in BadgeBrushes)
        {
            brush.Freeze();
        }
    }

    /// <summary>配色池容量，供视图模型分配下标时取模。</summary>
    internal static int PaletteSize => BadgeBrushes.Length;

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int index || index < 0 || index >= BadgeBrushes.Length)
        {
            return Brushes.Transparent;
        }

        return BadgeBrushes[index];
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
