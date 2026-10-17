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
///
/// 全仓库的船名翻译统一走 <see cref="Resolve"/>：它能同时接受数字 ID 和
/// 英文名，内部按 ID 优先、英文名兜底。不要再在别处另写一套判断，否则
/// 名单显示中文、明细显示英文这种口径不一致会反复出现。
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
    /// 英文名到中文名的反查表。
    /// </summary>
    /// <remarks>
    /// 回放里的逐船伤害明细只有英文船名与玩家 ID，没有可直接查表的数字船 ID，
    /// 只能靠英文名反查。重名时以排序靠前的条目为准，保证每次结果一致。
    /// </remarks>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> ChineseByEnglish =
        new(LoadEnglishIndex, LazyThreadSafetyMode.ExecutionAndPublication);

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
    /// 按数字船 ID 查中文名。
    /// </summary>
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
    /// 按英文船名查中文名。
    /// </summary>
    /// <remarks>
    /// 回放导出的船名是英文，与船目录的 name_en-us 一致。大小写与首尾空白
    /// 不敏感，其余标点必须完全一致。
    /// </remarks>
    public static bool TryGetChineseNameByEnglish(string englishName, out string? chinese)
    {
        chinese = null;
        if (string.IsNullOrWhiteSpace(englishName))
        {
            return false;
        }

        return ChineseByEnglish.Value.TryGetValue(englishName.Trim(), out chinese);
    }

    /// <summary>
    /// 统一的中文船名解析入口。
    /// </summary>
    /// <remarks>
    /// 先按数字船 ID 查，再按英文名查。两者都查不到时原样返回输入，
    /// 让界面至少能显示船 ID 或英文名，而不是空白。
    ///
    /// 调用方按这个顺序传参：有 ID 传 ID，没有 ID 就把英文名当 ID 传，
    /// 内部会自动尝到第二个分支。
    /// </remarks>
    /// <param name="shipIdNumeric">数字船 ID，可为空。</param>
    /// <param name="englishNameOrFallback">英文船名，作为最终兜底返回。</param>
    public static string Resolve(string? shipIdNumeric, string? englishNameOrFallback)
    {
        if (!string.IsNullOrWhiteSpace(shipIdNumeric)
            && TryGetChineseName(shipIdNumeric, out string? byId)
            && !string.IsNullOrEmpty(byId))
        {
            return byId;
        }

        if (!string.IsNullOrWhiteSpace(englishNameOrFallback))
        {
            if (TryGetChineseNameByEnglish(englishNameOrFallback, out string? byName)
                && !string.IsNullOrEmpty(byName))
            {
                return byName;
            }

            return englishNameOrFallback.Trim();
        }

        return string.Empty;
    }

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

    /// <summary>
    /// 构建英文名到中文名的反查表。
    /// </summary>
    private static IReadOnlyDictionary<string, string> LoadEnglishIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return index;
            }

            using var reader = new StreamReader(stream);
            var root = JObject.Parse(reader.ReadToEnd());
            if (root["ships"] is not JObject ships)
            {
                return index;
            }

            // 按船 ID 排序再插入，重名时保证每次都是同一个来源中译名。
            var ordered = ships.Properties()
                .OrderBy(property => property.Name, StringComparer.Ordinal);

            foreach (JProperty property in ordered)
            {
                string? english = property.Value["name_en-us"]?.Value<string>();
                string? chinese = property.Value["name_zh-cn"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(english) || string.IsNullOrEmpty(chinese))
                {
                    continue;
                }

                index.TryAdd(english.Trim(), chinese);
            }
        }
        catch (Exception)
        {
            // 反查表同样属于增强信息，失败保持空表。
        }

        return index;
    }
}
