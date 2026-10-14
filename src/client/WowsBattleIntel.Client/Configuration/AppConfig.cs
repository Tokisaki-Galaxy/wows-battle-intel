using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WowsBattleIntel.Client.Configuration;

/// <summary>
/// 应用配置。
/// </summary>
/// <remarks>
/// 存储在用户目录，不随安装更新被覆盖，见设计文档的不变量约定。
/// 路径：%APPDATA%\wows-battle-intel\config.json
/// </remarks>
public sealed class AppConfig
{
    /// <summary>游戏安装目录。</summary>
    public string GamePath { get; set; } = string.Empty;

    /// <summary>本地 SQLite 库路径。</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>replayshark 可执行文件路径。</summary>
    public string ReplaysharkPath { get; set; } = string.Empty;

    /// <summary>游戏数据目录，含各版本的 .def 定义。</summary>
    public string ExtractedDataPath { get; set; } = string.Empty;

    /// <summary>constants.json 所在根目录，按版本分子目录。</summary>
    public string ConstantsRootPath { get; set; } = string.Empty;

    /// <summary>ONNX 模型路径。为空时使用基线预测器。</summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>模型版本标识。</summary>
    public string ModelVersion { get; set; } = "baseline-logistic-v1";

    /// <summary>是否自动观测回放目录。</summary>
    public bool AutoObserve { get; set; } = true;

    /// <summary>观测轮询间隔秒。</summary>
    public int PollIntervalSeconds { get; set; } = 10;

    /// <summary>WG API 应用 ID。为空时用默认共享 ID。</summary>
    public string WgApplicationId { get; set; } = string.Empty;

    /// <summary>配置文件路径。</summary>
    [JsonIgnore]
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "wows-battle-intel",
        "config.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// 加载配置。文件不存在时返回带合理默认值的配置并写盘。
    /// </summary>
    public static AppConfig Load()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                string json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
            catch
            {
                // 配置损坏时退回默认值，不阻断启动。
            }
        }

        var config = CreateDefault();
        config.Save();
        return config;
    }

    /// <summary>
    /// 创建默认配置。尝试从已知位置推断路径。
    /// </summary>
    public static AppConfig CreateDefault()
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "wows-battle-intel");

        var config = new AppConfig
        {
            DatabasePath = Path.Combine(appData, "battles.db"),
        };

        // 探测常见的游戏安装位置。
        foreach (string candidate in GetGamePathCandidates())
        {
            if (Directory.Exists(candidate))
            {
                config.GamePath = candidate;
                break;
            }
        }

        return config;
    }

    /// <summary>
    /// 常见游戏安装目录候选。
    /// </summary>
    private static IEnumerable<string> GetGamePathCandidates()
    {
        yield return @"D:\SteamLibrary\steamapps\common\World of Warships";
        yield return @"C:\SteamLibrary\steamapps\common\World of Warships";
        yield return @"C:\Games\World_of_Warships";
        yield return @"D:\Games\World_of_Warships";
    }

    /// <summary>
    /// 保存配置。
    /// </summary>
    public void Save()
    {
        string? dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, SerializerOptions));
    }

    /// <summary>
    /// 回放目录。
    /// </summary>
    public string ReplayDirectory => Path.Combine(GamePath, "replays");

    /// <summary>
    /// 配置是否足够开始工作。
    /// </summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(GamePath)
        && Directory.Exists(GamePath);
}
