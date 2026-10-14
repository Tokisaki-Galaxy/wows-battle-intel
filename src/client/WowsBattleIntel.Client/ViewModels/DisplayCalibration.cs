namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 预测概率的展示校准。
/// </summary>
/// <remarks>
/// 逻辑回归在特征共线时会外推输出极端概率，直接显示 100% 或 0% 会误导。
/// 这里提供两层处理：
/// 1. 显示值夹到 [0.05, 0.95]，入库的概率保持原始值，截断只发生在展示层；
/// 2. 按概率分档给出友方视角的态势文案，数字之外提供可读结论。
/// </remarks>
public static class DisplayCalibration
{
    /// <summary>展示概率下限。</summary>
    public const double DisplayFloor = 0.05;

    /// <summary>展示概率上限。</summary>
    public const double DisplayCeiling = 0.95;

    /// <summary>
    /// 把概率夹到展示区间。入库值不经过此方法，截断只影响界面读数。
    /// </summary>
    public static double ClampForDisplay(double probability) =>
        Math.Clamp(probability, DisplayFloor, DisplayCeiling);

    /// <summary>
    /// 按友方获胜概率分档，返回态势文案。
    /// </summary>
    public static string TierText(double allyWinProbability) => allyWinProbability switch
    {
        >= 0.7 => "明显优势",
        >= 0.55 => "略占优",
        > 0.45 => "势均力敌",
        > 0.3 => "处于劣势",
        _ => "明显劣势",
    };

    /// <summary>
    /// 分档对应的强调色资源键，供界面直接绑定。
    /// </summary>
    public static string TierBrushKey(double allyWinProbability) => allyWinProbability switch
    {
        >= 0.7 => "WinBrush",
        >= 0.55 => "AllyBrush",
        > 0.45 => "TextSecondaryBrush",
        > 0.3 => "HiddenBrush",
        _ => "LossBrush",
    };
}
