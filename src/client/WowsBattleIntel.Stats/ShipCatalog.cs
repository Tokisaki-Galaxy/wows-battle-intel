using System.Reflection;
using Newtonsoft.Json.Linq;

namespace WowsBattleIntel.Stats;

/// <summary>
/// 船名词典，数字船 ID 到中文船名。
/// </summary>
/// <remarks>
/// 数据取自 ApeRadar 随包分发的 ships.json（游戏数据，GPL 代码不传染），
/// 以嵌入资源形式随程序集携带。词典里没有的 ID 多为新船或超测船，
/// 查询失败走回落逻辑，不视为异常。初始化失败时静默降级为空词典。
/// </remarks>
public static class ShipCatalog
{
    private const string ResourceName = "WowsBattleIntel.Stats.Resources.ships.json";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> ChineseNames =
        new(LoadChineseNames, LazyThreadSafetyMode.ExecutionAndPublication);

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
    /// 从嵌入资源加载词典。任何失败都返回空字典，主流程不受影响。
    /// </summary>
    private static IReadOnlyDictionary<string, string> LoadChineseNames()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return new Dictionary<string, string>();
            }

            using var reader = new StreamReader(stream);
            var root = JObject.Parse(reader.ReadToEnd());
            var ships = root["ships"] as JObject;
            if (ships is null)
            {
                return new Dictionary<string, string>();
            }

            var result = new Dictionary<string, string>(ships.Count, StringComparer.Ordinal);
            foreach (var (shipId, token) in ships)
            {
                // 只取中文名，国籍与舰种字段暂不消费。
                var zh = token?["name_zh-cn"]?.Value<string>();
                if (!string.IsNullOrEmpty(zh) && shipId is not null)
                {
                    result[shipId] = zh;
                }
            }

            return result;
        }
        catch (Exception)
        {
            // 词典属于增强信息，加载失败静默降级为空词典。
            return new Dictionary<string, string>();
        }
    }
}
