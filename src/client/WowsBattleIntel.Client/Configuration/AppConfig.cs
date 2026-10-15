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

    /// <summary>快照保留天数，默认 14，0 表示不清理。</summary>
    public int SnapshotRetentionDays { get; set; } = 14;

    /// <summary>WG API 应用 ID。为空时用默认共享 ID。</summary>
    public string WgApplicationId { get; set; } = string.Empty;

    /// <summary>主题：system 跟随系统、light 浅色、dark 深色。默认跟随系统。</summary>
    public string Theme { get; set; } = "system";

    /// <summary>自动更新开关。默认开启，关闭后启动不再检查新版本。</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// GitHub 访问令牌，供私有仓库期间拉取更新 feed。可空，仓库转 public 后留空即可。
    /// </summary>
    public string GithubToken { get; set; } = string.Empty;

    /// <summary>配置文件路径。</summary>
    /// <remarks>
    /// 测试可通过 <see cref="ConfigPathOverride"/> 重定向，避免测试写盘污染真实配置。
    /// </remarks>
    [JsonIgnore]
    public static string ConfigPath => ConfigPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "wows-battle-intel",
        "config.json");

    /// <summary>测试专用的配置路径覆盖。设置后 Load 与 Save 都指向该路径。</summary>
    [JsonIgnore]
    internal static string? ConfigPathOverride { get; set; }

    /// <summary>GamePath 未配置时回放目录的回退位置。</summary>
    private const string DefaultReplayDirectory = @"D:\replays";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// 加载配置。文件不存在时返回带合理默认值的配置并写盘。
    /// </summary>
    /// <remarks>
    /// 已存在的配置文件优先于默认值：旧字段沿用文件里的值，仅对新字段补默认，
    /// 绝不用默认值覆盖用户已配置的路径。
    /// </remarks>
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
                    loaded.Normalize();
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
    /// 规范化配置：把空值补成推断出的默认路径，绝对化相对路径。
    /// </summary>
    /// <remarks>
    /// 只在值为空或相对时才动，用户显式配置的绝对路径原样保留。
    /// </remarks>
    internal void Normalize()
    {
        GamePath = ResolveGamePath(GamePath);
        DatabasePath = ResolveDatabasePath(DatabasePath);
        ModelPath = ResolveModelPath(ModelPath);
    }

    /// <summary>
    /// 解析游戏目录。空值时先跑自动探测（运行中进程与卸载注册表），
    /// 探测不到再退回常见安装位置，全部失败保持为空。
    /// </summary>
    private static string ResolveGamePath(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return ToAbsolutePath(value);
        }

        // 正式探测：进程优先，其次卸载注册表。命中即带 replays 校验。
        var detected = Services.GamePathDetector.Detect();
        if (detected is not null)
        {
            return detected.GamePath;
        }

        foreach (string candidate in GetGamePathCandidates())
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 解析数据库路径。空值时推断仓库 data 目录下的默认库文件。
    /// </summary>
    /// <remarks>
    /// 测试运行曾把临时库路径写进真实配置，形如 wbi-client-test-*.db。
    /// 这类值来自测试而不是用户，识别后按缺失处理重新推断。
    /// </remarks>
    private static string ResolveDatabasePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsTestArtifactDatabasePath(value))
        {
            return InferDatabasePath();
        }

        return ToAbsolutePath(value);
    }

    /// <summary>
    /// 判断是否为测试遗留的临时库路径。
    /// </summary>
    private static bool IsTestArtifactDatabasePath(string value) =>
        value.Contains("wbi-client-test-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 推断默认数据库路径：优先仓库 data/battles.db，其次退回用户目录。
    /// </summary>
    private static string InferDatabasePath()
    {
        string? repoDataDir = FindRepoDataDirectory();
        if (repoDataDir is not null)
        {
            return Path.Combine(repoDataDir, "battles.db");
        }

        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "wows-battle-intel");
        return Path.Combine(appData, "battles.db");
    }

    /// <summary>
    /// 解析模型路径。空值时探测仓库 data/models 或发布包 models 下的默认模型。
    /// </summary>
    private static string ResolveModelPath(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return ToAbsolutePath(value);
        }

        // 空值时探测仓库默认模型，找不到就保持为空，走基线预测器。
        string? repoDataDir = FindRepoDataDirectory();
        if (repoDataDir is not null)
        {
            string candidate = Path.Combine(repoDataDir, "models", "baseline.onnx");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 发布包场景：模型随包分发在主程序同级 models 目录。
        // 单文件发布的 Assembly.Location 为空，必须用 AppContext.BaseDirectory。
        string packageCandidate = Path.Combine(
            AppContext.BaseDirectory, "models", "baseline.onnx");
        if (File.Exists(packageCandidate))
        {
            return packageCandidate;
        }

        return string.Empty;
    }

    /// <summary>
    /// 相对路径按当前工作目录绝对化，绝对路径原样返回。
    /// </summary>
    private static string ToAbsolutePath(string value) =>
        Path.IsPathRooted(value) ? value : Path.GetFullPath(value);

    /// <summary>
    /// 从应用基目录向上查找仓库的 data 目录。
    /// </summary>
    /// <remarks>
    /// 开发场景下应用基目录位于 仓库\src\client\*\bin\Debug\net10.0-windows，
    /// 逐级上溯最多十层即可覆盖仓库根。找不到返回 null。
    /// 单文件发布下 Assembly.Location 为空，必须用 AppContext.BaseDirectory。
    /// </remarks>
    private static string? FindRepoDataDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; dir is not null && i < 10; i++)
        {
            string candidate = Path.Combine(dir, "data");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    /// 创建默认配置。尝试从已知位置推断路径。
    /// </summary>
    public static AppConfig CreateDefault()
    {
        var config = new AppConfig
        {
            GamePath = ResolveGamePath(string.Empty),
            DatabasePath = ResolveDatabasePath(string.Empty),
            ModelPath = ResolveModelPath(string.Empty),
        };

        return config;
    }

    /// <summary>
    /// 常见游戏安装目录候选，作为自动探测失败后的兜底。
    /// </summary>
    private static IEnumerable<string> GetGamePathCandidates()
    {
        yield return @"D:\SteamLibrary\steamapps\common\World of Warships";
        yield return @"C:\SteamLibrary\steamapps\common\World of Warships";
        yield return @"C:\Games\World_of_Warships";
        yield return @"D:\Games\World_of_Warships";
        yield return @"C:\Games\World of Warships";
        yield return @"D:\Games\World of Warships";
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
    /// <remarks>
    /// 优先用配置的 GamePath；未配置时回退到常见回放位置，
    /// 避免拼出依赖进程工作目录的相对路径。
    /// </remarks>
    [JsonIgnore]
    public string ReplayDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(GamePath))
            {
                return Path.Combine(GamePath, "replays");
            }

            return DefaultReplayDirectory;
        }
    }

    /// <summary>
    /// 配置是否足够开始工作。
    /// </summary>
    /// <remarks>
    /// 数据库路径始终有推断默认值，只要回放目录可用即可开始观测。
    /// </remarks>
    public bool IsUsable => !string.IsNullOrWhiteSpace(ReplayDirectory)
        && Directory.Exists(ReplayDirectory);
}
