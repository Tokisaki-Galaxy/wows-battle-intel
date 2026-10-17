using System.Globalization;
using System.Windows.Media;
using WowsBattleIntel.Client.Views.Converters;

namespace WowsBattleIntel.Client.Tests;

/// <summary>
/// 组队色块的用例。
/// </summary>
/// <remarks>
/// 回归背景：早期版本按组队人数取色，同一局里两个双人队会撞成同一个颜色。
/// 那时一场比赛中本方出现 4 个黄块，实际分属 2 个互不相干的小队。
/// 现在下标由视图模型按整场比赛范围分配，转换器只负责查表。
/// </remarks>
public class DivisionBadgeBrushConverterTests
{
    private readonly DivisionBadgeBrushConverter converter = new();

    [Fact]
    public void 不着色返回透明()
    {
        Assert.Same(Brushes.Transparent, Convert(-1));
    }

    [Fact]
    public void 超出配色池返回透明()
    {
        Assert.Same(Brushes.Transparent, Convert(DivisionBadgeBrushConverter.PaletteSize));
        Assert.Same(Brushes.Transparent, Convert(9999));
    }

    [Fact]
    public void 下标相同的行同色()
    {
        Assert.Same(Convert(0), Convert(0));
        Assert.Same(Convert(3), Convert(3));
    }

    [Fact]
    public void 不同下标必然不同色()
    {
        // 这是修复的核心：两个双人队人数相同，但会拿到不同下标。
        object? first = Convert(0);
        object? second = Convert(1);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void 配色池内两两不重复()
    {
        var seen = new HashSet<Brush>();
        for (int i = 0; i < DivisionBadgeBrushConverter.PaletteSize; i++)
        {
            var brush = (Brush)Convert(i);
            Assert.True(seen.Add(brush), $"下标 {i} 与已有颜色重复");
        }
    }

    private object Convert(int index) =>
        converter.Convert(index, typeof(Brush), null, CultureInfo.InvariantCulture);
}
