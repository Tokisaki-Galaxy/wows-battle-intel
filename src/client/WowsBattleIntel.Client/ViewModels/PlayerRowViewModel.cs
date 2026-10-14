using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 单个玩家的展示模型。
/// </summary>
/// <remarks>
/// 把领域模型转成界面友好的形式，附带显示用的格式化文本与颜色标记。
/// </remarks>
public sealed class PlayerRowViewModel : ViewModelBase
{
    private PlayerRecord? source;

    /// <summary>
    /// 从领域模型构造。
    /// </summary>
    public static PlayerRowViewModel FromPlayer(PlayerRecord player)
    {
        ArgumentNullException.ThrowIfNull(player);

        var vm = new PlayerRowViewModel { source = player };

        // 组队标记：单排不显示，双排显示 2，三排显示 3。
        vm.DivisionMark = player.DivisionSize switch
        {
            2 => "双排",
            3 => "三排",
            _ => string.Empty,
        };

        vm.WinrateText = player.AccountWinrate >= 0
            ? player.AccountWinrate.ToString("P1")
            : "隐藏";

        vm.BattlesText = player.AccountBattles >= 0
            ? ((int)player.AccountBattles).ToString("N0")
            : "-";

        vm.ShipText = string.IsNullOrEmpty(player.ShipName)
            ? player.ShipId
            : player.ShipName;

        vm.ShipTypeText = $"{ShipClassLabel(player.ShipClass)} {player.ShipTier}";

        return vm;
    }

    /// <summary>昵称。</summary>
    public string PlayerName => source?.PlayerName ?? string.Empty;

    /// <summary>公会标签。</summary>
    public string ClanTag => source?.ClanTag ?? string.Empty;

    /// <summary>船名。</summary>
    public string ShipText { get; private set; } = string.Empty;

    /// <summary>舰种与等级。</summary>
    public string ShipTypeText { get; private set; } = string.Empty;

    /// <summary>胜率显示文本。</summary>
    public string WinrateText { get; private set; } = string.Empty;

    /// <summary>场次显示文本。</summary>
    public string BattlesText { get; private set; } = string.Empty;

    /// <summary>组队标记。</summary>
    public string DivisionMark { get; private set; } = string.Empty;

    /// <summary>是否隐藏战绩。</summary>
    public bool IsHidden => source?.IsHidden ?? false;

    /// <summary>是否机器人。</summary>
    public bool IsBot => source?.IsBot ?? false;

    /// <summary>
    /// 水平分类标签。
    /// </summary>
    public string TierLabel
    {
        get
        {
            if (source is null || !source.HasStats)
            {
                return "隐藏";
            }

            double wr = source.AccountWinrate;
            // 阈值沿用 ApeRadar 默认口径。
            if (wr > 0.58 && source.AccountBattles >= 500)
            {
                return "海王";
            }

            return wr <= 0.47 ? "猴子" : "普通";
        }
    }

    /// <summary>
    /// 水平分类的颜色资源键。
    /// </summary>
    /// <remarks>
    /// 海王用金色、猴子用紫色，普通与隐藏战绩用灰。具体色值由主题资源决定，
    /// 这里只给语义键名，经 BrushKeyConverter 换成画刷。
    /// </remarks>
    public string TierBrushKey => TierLabel switch
    {
        "海王" => "UnicumBrush",
        "猴子" => "ApeBrush",
        _ => "HiddenBrush",
    };

    /// <summary>
    /// 胜率列的颜色资源键。高低两端给语义色，中间保持默认文本色。
    /// </summary>
    public string WinrateBrushKey
    {
        get
        {
            if (source is null || !source.HasStats)
            {
                return "HiddenBrush";
            }

            // 与 TierLabel 同一套阈值，颜色跟随分类口径。
            double wr = source.AccountWinrate;
            if (wr > 0.58 && source.AccountBattles >= 500)
            {
                return "UnicumBrush";
            }

            return wr <= 0.47 ? "ApeBrush" : "TextPrimaryBrush";
        }
    }

    /// <summary>
    /// 本局伤害显示。
    /// </summary>
    public string DamageText => source is null
        ? "-"
        : source.DamageDealt > 0 ? source.DamageDealt.ToString("N0") : "-";

    /// <summary>
    /// 本局击杀显示。
    /// </summary>
    public string KillsText => source is null || source.Kills == 0
        ? "-"
        : source.Kills.ToString();

    private static string ShipClassLabel(ShipClass shipClass) => shipClass switch
    {
        ShipClass.Destroyer => "DD",
        ShipClass.Cruiser => "CA",
        ShipClass.Battleship => "BB",
        ShipClass.Carrier => "CV",
        ShipClass.Submarine => "SS",
        _ => "??",
    };
}
