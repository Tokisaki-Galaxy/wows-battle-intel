namespace WowsBattleIntel.Core.Scoring;

/// <summary>
/// 加权胜率计算。
/// </summary>
/// <remarks>
/// 口径沿用 ApeRadar 的 CalcWeightedWinrate，是兼容契约，不要改动，
/// 以便与现有工具的输出对照。
///
/// 设计意图：单人打出来的胜率含金量更高，因为组队协同会抬高胜率，
/// 掩盖个人水平。因此单人场次权重最高，三排最低。单船数据最多占两成
/// 权重，避免样本稀少的单船统计主导整体判断。
/// </remarks>
public static class WeightedWinrate
{
    /// <summary>单人场次权重。</summary>
    public const double DefaultSoloWeightMultiplier = 5.0;

    /// <summary>双排场次权重。</summary>
    public const double DefaultDiv2WeightMultiplier = 2.0;

    /// <summary>三排场次权重。</summary>
    public const double DefaultDiv3WeightMultiplier = 1.0;

    /// <summary>单船数据的最大权重占比。</summary>
    public const double DefaultShipMaxWeight = 20.0;

    /// <summary>达到最大单船权重所需的单船场次。</summary>
    public const double DefaultShipBattlesAtMaxWeight = 60.0;

    /// <summary>
    /// 计算加权胜率。
    /// </summary>
    /// <param name="accountWinrateSolo">单人胜率。</param>
    /// <param name="accountBattlesSolo">单人场次。</param>
    /// <param name="accountWinrateDiv2">双排胜率。</param>
    /// <param name="accountBattlesDiv2">双排场次。</param>
    /// <param name="accountWinrateDiv3">三排胜率。</param>
    /// <param name="accountBattlesDiv3">三排场次。</param>
    /// <param name="shipWinrate">单船胜率。</param>
    /// <param name="shipBattles">单船场次。</param>
    /// <param name="options">权重参数，默认取 ApeRadar 口径。</param>
    /// <returns>加权胜率。输入场次全为零时返回账号胜率的加权平均或 0。</returns>
    public static double Compute(
        double accountWinrateSolo,
        double accountBattlesSolo,
        double accountWinrateDiv2,
        double accountBattlesDiv2,
        double accountWinrateDiv3,
        double accountBattlesDiv3,
        double shipWinrate,
        double shipBattles,
        WeightedWinrateOptions? options = null)
    {
        var opt = options ?? WeightedWinrateOptions.Default;

        double accountSoloWeight = accountBattlesSolo * opt.SoloWeightMultiplier;
        double accountDiv2Weight = accountBattlesDiv2 * opt.Div2WeightMultiplier;
        double accountDiv3Weight = accountBattlesDiv3 * opt.Div3WeightMultiplier;
        double accountWeightTotal = accountSoloWeight + accountDiv2Weight + accountDiv3Weight;

        double accountWeightedWinrate = accountWeightTotal <= 0
            ? 0
            : (accountWinrateSolo * accountSoloWeight
               + accountWinrateDiv2 * accountDiv2Weight
               + accountWinrateDiv3 * accountDiv3Weight) / accountWeightTotal;

        double shipWeight = (shipBattles >= opt.ShipBattlesAtMaxWeight
            ? opt.ShipMaxWeight
            : opt.ShipMaxWeight * shipBattles / opt.ShipBattlesAtMaxWeight) / 100.0;

        return accountWeightedWinrate * (1 - shipWeight) + shipWinrate * shipWeight;
    }

    /// <summary>
    /// 从玩家记录计算加权胜率，未知字段按哨兵语义跳过。
    /// </summary>
    public static double Compute(Models.PlayerRecord player, WeightedWinrateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(player);

        double soloWr = NonNegative(player.AccountWinrateSolo);
        double soloB = NonNegative(player.AccountBattlesSolo);
        double div2Wr = NonNegative(player.AccountWinrateDiv2);
        double div2B = NonNegative(player.AccountBattlesDiv2);
        double div3Wr = NonNegative(player.AccountWinrateDiv3);
        double div3B = NonNegative(player.AccountBattlesDiv3);
        double shipWr = NonNegative(player.ShipWinrate);
        double shipB = NonNegative(player.ShipBattles);

        if (soloB + div2B + div3B <= 0 && shipB <= 0)
        {
            // 没有细分数据时退回账号总胜率。
            return NonNegative(player.AccountWinrate);
        }

        return Compute(soloWr, soloB, div2Wr, div2B, div3Wr, div3B, shipWr, shipB, options);
    }

    private static double NonNegative(double value) => value < 0 ? 0 : value;
}

/// <summary>
/// 加权胜率的权重参数。
/// </summary>
public sealed record WeightedWinrateOptions
{
    /// <summary>默认参数，取 ApeRadar 口径。</summary>
    public static WeightedWinrateOptions Default { get; } = new();

    /// <summary>单人场次权重。</summary>
    public double SoloWeightMultiplier { get; init; } = WeightedWinrate.DefaultSoloWeightMultiplier;

    /// <summary>双排场次权重。</summary>
    public double Div2WeightMultiplier { get; init; } = WeightedWinrate.DefaultDiv2WeightMultiplier;

    /// <summary>三排场次权重。</summary>
    public double Div3WeightMultiplier { get; init; } = WeightedWinrate.DefaultDiv3WeightMultiplier;

    /// <summary>单船数据的最大权重占比。</summary>
    public double ShipMaxWeight { get; init; } = WeightedWinrate.DefaultShipMaxWeight;

    /// <summary>达到最大单船权重所需的单船场次。</summary>
    public double ShipBattlesAtMaxWeight { get; init; } = WeightedWinrate.DefaultShipBattlesAtMaxWeight;
}
