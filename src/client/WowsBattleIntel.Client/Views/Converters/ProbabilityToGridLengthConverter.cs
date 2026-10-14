using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WowsBattleIntel.Client.Views.Converters;

/// <summary>
/// 把友方预测概率转成概率条的分割位置。
/// </summary>
/// <remarks>
/// 概率条用两列 Grid 实现，友方蓝与敌方红各占一列。
/// 友方列宽由该转换器给出星比例，敌方列占满剩余空间。
/// 概率为 0.5 时两列等宽，分割线恰好落在中点。
/// </remarks>
public sealed class ProbabilityToGridLengthConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 夹到 0 到 1，异常数据不至于把条画反。
        double ratio = value is double p ? Math.Clamp(p, 0.0, 1.0) : 0.5;
        return new GridLength(ratio, GridUnitType.Star);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("概率条宽度由视图模型单向提供，不支持反向转换。");
}
