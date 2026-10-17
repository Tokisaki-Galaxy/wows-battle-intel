using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Scoring;

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

    /// <summary>观测轮询间隔秒。</summary>
    public int PollIntervalSeconds { get; set; } = 10;

    /// <summary>快照保留天数，默认 14，0 表示不清理。</summary>
    public int SnapshotRetentionDays { get; set; } = 14;

    /// <summary>
    /// 是否缓存对局战绩加速查询。开启后同一玩家在缓存有效期内的重复查询
    /// 直接命中本地，不再请求 WG API。默认开启。
    /// </summary>
    public bool EnableBattleCache { get; set; } = true;

    /// <summary>
    /// 战绩缓存有效期天数，默认 7。0 表示不设新鲜度上界，永不按时间过期。
    /// </summary>
    public int BattleCacheDays { get; set; } = 7;

    /// <summary>
    /// 名单整行染色规则。按顺序求值，第一条命中的决定行色。
    /// </summary>
    /// <remarks>
    /// 默认两条：胜率大于等于 0.58 染紫、胜率小于等于 0.47 染红，即原有的
    /// 紫色大佬与红色海猴口径。规则可空，为空时名单不按水平染底色。
    /// </remarks>
    public List<ColorRule> ColorRules { get; set; } = DefaultColorRules.Clone();

    /// <summary>
    /// 默认染色规则。紫规则在前，保证高胜率玩家判紫优先于红。
    /// </summary>
    public static List<ColorRule> CreateDefaultColorRules() => DefaultColorRules.Clone();

    /// <summary>WG API 应用 ID。为空时用默认共享 ID。</summary>
    public string WgApplicationId { get; set; } = string.Empty;

    /// <summary>主题：system 跟随系统、light 浅色、dark 深色。默认跟随系统。</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// 日志目录。为空时按仓库 Logs 目录推断，发布包场景退回用户目录。
    /// </summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>日志保留天数，默认 3。0 表示不限天数。</summary>
    public int LogRetentionDays { get; set; } = 3;

    /// <summary>日志总量上限 MB，默认 10。0 表示不限体积。</summary>
    public int LogMaxSizeMb { get; set; } = 10;

    /// <summary>
    /// 清理口径：both 天数与体积同时生效、days 只看天数、size 只看体积。默认 both。
    /// </summary>
    public string LogRetentionMode { get; set; } = "both";

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
        ReplaysharkPath = ResolveReplaysharkPath(ReplaysharkPath);
        ExtractedDataPath = ResolveExtractedDataPath(ExtractedDataPath);
        ConstantsRootPath = ResolveConstantsRootPath(ConstantsRootPath);
        LogDirectory = ResolveLogDirectory(LogDirectory);

        // 旧配置无染色规则字段，反序列化后为 null，补默认；显式存过 null
        // 也按缺失处理，避免名单着色被清空。
        ColorRules ??= CreateDefaultColorRules();
    }

    /// <summary>
    /// 解析 replayshark 可执行文件。空值或路径失效时探测发布包 tools 目录
    /// 与仓库 data/tools。
    /// </summary>
    private static string ResolveReplaysharkPath(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
        {
            return ToAbsolutePath(value);
        }

        foreach (string candidate in GetReplaysharkCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 解析游戏数据根目录（各版本子目录的父目录）。
    /// </summary>
    private static string ResolveExtractedDataPath(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
        {
            return ToAbsolutePath(value);
        }

        foreach (string candidate in GetDataRootCandidates("gamedata"))
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 解析 constants 根目录（各版本子目录的父目录）。
    /// </summary>
    private static string ResolveConstantsRootPath(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
        {
            return ToAbsolutePath(value);
        }

        foreach (string candidate in GetDataRootCandidates("constants"))
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// replayshark.exe 候选位置。发布包 tools 目录优先，其次仓库 data/tools。
    /// </summary>
    private static IEnumerable<string> GetReplaysharkCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "replayshark.exe");
        string? repoDataDir = FindRepoDataDirectory();
        if (repoDataDir is not null)
        {
            yield return Path.Combine(repoDataDir, "tools", "replayshark.exe");
        }
    }

    /// <summary>
    /// 游戏数据与 constants 根目录候选。发布包内目录优先，其次仓库 data。
    /// </summary>
    private static IEnumerable<string> GetDataRootCandidates(string subdirectory)
    {
        yield return Path.Combine(AppContext.BaseDirectory, subdirectory);
        string? repoDataDir = FindRepoDataDirectory();
        if (repoDataDir is not null)
        {
            yield return Path.Combine(repoDataDir, subdirectory);
        }
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
    /// 解析日志目录。空值时优先用仓库根下的 Logs，发布包场景退回用户目录。
    /// </summary>
    /// <remarks>
    /// 目录允许暂时不存在，写入器会按需创建，因此这里不做存在性校验。
    /// </remarks>
    private static string ResolveLogDirectory(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return ToAbsolutePath(value);
        }

        return InferLogDirectory();
    }

    /// <summary>
    /// 推断默认日志目录：优先仓库根 Logs，找不到仓库时退回用户目录。
    /// </summary>
    public static string InferLogDirectory()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is not null)
        {
            return Path.Combine(repoRoot, "Logs");
        }

        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "wows-battle-intel");
        return Path.Combine(appData, "Logs");
    }

    /// <summary>
    /// 从应用基目录向上查找仓库根目录，找不到返回 null。
    /// </summary>
    private static string? FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; dir is not null && i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "WowsBattleIntel.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
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
            LogDirectory = InferLogDirectory(),
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
    /// 配置字符串转清理口径。未知值按同时生效处理。
    /// </summary>
    public static LogRetentionMode ParseRetentionMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "days" => Configuration.LogRetentionMode.Days,
            "size" => Configuration.LogRetentionMode.Size,
            _ => Configuration.LogRetentionMode.Both,
        };

    /// <summary>
    /// 清理口径转配置字符串。
    /// </summary>
    public static string RetentionModeValue(LogRetentionMode mode) =>
        mode switch
        {
            Configuration.LogRetentionMode.Days => "days",
            Configuration.LogRetentionMode.Size => "size",
            _ => "both",
        };

    /// <summary>
    /// 按当前配置生成日志写入选项。
    /// </summary>
    /// <remarks>
    /// 负数的天数与体积按 0 处理，0 表示该维度不限，避免写入器拿到无意义的负值。
    /// </remarks>
    [JsonIgnore]
    public LogFileOptions LogOptions => new(
        LogDirectory,
        Math.Max(0, LogRetentionDays),
        Math.Max(0, LogMaxSizeMb) * 1024L * 1024L,
        ParseRetentionMode(LogRetentionMode));

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
    /// 战绩缓存有效期。0 天表示不设新鲜度上界，取任意最新快照。
    /// </summary>
    /// <remarks>
    /// 负数按 0 处理，与界面校验的口径一致，避免装饰器拿到负 TTL。
    /// </remarks>
    [JsonIgnore]
    public TimeSpan BattleCacheTtl => BattleCacheDays <= 0
        ? TimeSpan.MaxValue
        : TimeSpan.FromDays(BattleCacheDays);

    /// <summary>
    /// 配置是否足够开始工作。
    /// </summary>
    /// <remarks>
    /// 数据库路径始终有推断默认值，只要回放目录可用即可开始观测。
    /// </remarks>
    public bool IsUsable => !string.IsNullOrWhiteSpace(ReplayDirectory)
        && Directory.Exists(ReplayDirectory);
}
