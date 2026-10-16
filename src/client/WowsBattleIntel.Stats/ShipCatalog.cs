using Newtonsoft.Json.Linq;
using System.Reflection;

namespace WowsBattleIntel.Stats;

/// <summary>
/// 船目录：数字船 ID 到中文名、等级与舰种的静态映射。
/// </summary>
/// <remarks>
/// 数据取自 ApeRadar 随包分发的 ships.json（游戏数据，GPL 代码不传染），
/// 以嵌入资源随程序集分发。查询失败走回落逻辑，不视为异常。初始化失败时
/// 静默降级为空词典。
/// </remarks>
public static class ShipCatalog
{
    private const string ResourceName = "WowsBattleIntel.Stats.Resources.ships.json";

    /// <summary>船目录元数据条目：中文名、等级与舰种。</summary>
    public sealed record ShipMeta(string ChineseName, int Tier, string Type);

    private static readonly Lazy<IReadOnlyDictionary<string, ShipMeta>> Ships =
        new(LoadShips, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> ChineseNames =
        new(() => Ships.Value.ToDictionary(
            kv => kv.Key, kv => kv.Value.ChineseName, StringComparer.Ordinal),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 按数字船 ID 查完整元数据：中文名、等级与舰种。
    /// </summary>
    /// <remarks>
    /// tempArenaInfo 的 vehicles 自 15.9 起不再带 shipType/shipTier 字段，
    /// 赛前名单的舰种与等级靠这里回填。
    /// </remarks>
    public static bool TryGetMeta(string shipIdNumeric, out ShipMeta? meta)
    {
        meta = null;
        if (string.IsNullOrWhiteSpace(shipIdNumeric))
        {
            return false;
        }

        return Ships.Value.TryGetValue(shipIdNumeric.Trim(), out meta);
    }

    /// <summary>
    /// 按数字船 ID 查中文船名。
    /// </summary>
    /// <param name="shipIdNumeric">数字船 ID 字符串，如 "4286581900"。</param>
    /// <param name="chinese">中文船名，查不到为 null。</param>
    /// <returns>查到返回 true。</returns>
    public static bool TryGetChineseName(string shipIdNumeric, out string? chinese)
    {
        chinese = null;
        if (string.IsNullOrWhiteSpace(shipIdNumeric))
        {
            return false;
        }

        return ChineseNames.Value.TryGetValue(shipIdNumeric.Trim(), out chinese);
    }

    /// <summary>
    /// 按数字船 ID 查中文名，不区分输入格式细节，直接转发到主查询。
    /// </summary>
    public static bool TryGet(string shipIdNumeric, out string? chinese)
        => TryGetChineseName(shipIdNumeric, out chinese);

    /// <summary>
    /// 从嵌入资源加载船目录。任何失败都返回空字典，主流程不受影响。
    /// </summary>
    private static IReadOnlyDictionary<string, ShipMeta> LoadShips()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return new Dictionary<string, ShipMeta>();
            }

            using var reader = new StreamReader(stream);
            var root = JObject.Parse(reader.ReadToEnd());
            var ships = root["ships"] as JObject;
            if (ships is null)
            {
                return new Dictionary<string, ShipMeta>();
            }

            var result = new Dictionary<string, ShipMeta>(ships.Count, StringComparer.Ordinal);
            foreach (var (shipId, token) in ships)
            {
                if (token is null)
                {
                    continue;
                }

                var zh = token["name_zh-cn"]?.Value<string>();
                if (string.IsNullOrEmpty(zh) || shipId is null)
                {
                    continue;
                }

                var tier = token["tier"]?.Value<int>() ?? 0;
                var type = token["type"]?.Value<string>() ?? string.Empty;
                result[shipId] = new ShipMeta(zh, tier, type);
            }

            return result;
        }
        catch (Exception)
        {
            // 词典属于增强信息，加载失败静默降级为空词典。
            return new Dictionary<string, ShipMeta>();
        }
    }
}
