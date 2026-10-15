using System.IO;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Client.Services;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 配置视图模型。
/// </summary>
/// <remarks>
/// 把配置项做成可编辑字段，保存时写盘并触发服务重配置。路径类配置在构造
/// 时就被各服务捕获，改了必须重建服务，因此保存不是简单赋值。
/// </remarks>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppServices services;
    private readonly IPathPicker picker;

    private string gamePath = string.Empty;
    private string databasePath = string.Empty;
    private string replaysharkPath = string.Empty;
    private string extractedDataPath = string.Empty;
    private string constantsRootPath = string.Empty;
    private string modelPath = string.Empty;
    private string modelVersion = string.Empty;
    private string wgApplicationId = string.Empty;
    private bool autoObserve = true;
    private int pollIntervalSeconds = 10;
    private int themeIndex;
    private string statusMessage = string.Empty;
    private bool isBusy;
    private bool hasUnsavedChanges;

    /// <summary>
    /// 构造视图模型，字段初值取自当前配置。
    /// </summary>
    public SettingsViewModel(AppServices services, IPathPicker picker)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(picker);

        this.services = services;
        this.picker = picker;

        LoadFrom(services.Config);

        BrowseGamePathCommand = SyncCommand(_ => BrowseFolder(
            "选择游戏安装目录", GamePath, p => GamePath = p));
        BrowseDatabasePathCommand = SyncCommand(_ => BrowseFile(
            "选择数据库文件", "SQLite 数据库 (*.db)|*.db|所有文件 (*.*)|*.*", DatabasePath,
            p => DatabasePath = p));
        BrowseReplaysharkPathCommand = SyncCommand(_ => BrowseFile(
            "选择 replayshark 可执行文件", "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            ReplaysharkPath, p => ReplaysharkPath = p));
        BrowseExtractedDataPathCommand = SyncCommand(_ => BrowseFolder(
            "选择游戏数据目录", ExtractedDataPath, p => ExtractedDataPath = p));
        BrowseConstantsRootPathCommand = SyncCommand(_ => BrowseFolder(
            "选择 constants 根目录", ConstantsRootPath, p => ConstantsRootPath = p));
        BrowseModelPathCommand = SyncCommand(_ => BrowseFile(
            "选择 ONNX 模型", "ONNX 模型 (*.onnx)|*.onnx|所有文件 (*.*)|*.*", ModelPath,
            p => ModelPath = p));

        SaveCommand = new RelayCommand(
            async _ => await SaveAsync().ConfigureAwait(true),
            _ => !IsBusy);
    }

    /// <summary>
    /// 构造一个同步命令。
    /// </summary>
    private static RelayCommand SyncCommand(Action<object?> action) =>
        new(_ =>
        {
            action(_);
            return Task.CompletedTask;
        });

    /// <summary>配置已应用并完成服务重配置后触发。</summary>
    public event Action? SettingsApplied;

    /// <summary>游戏安装目录。</summary>
    public string GamePath
    {
        get => gamePath;
        set => SetFieldAndMarkDirty(ref gamePath, value);
    }

    /// <summary>本地数据库路径。</summary>
    public string DatabasePath
    {
        get => databasePath;
        set => SetFieldAndMarkDirty(ref databasePath, value);
    }

    /// <summary>replayshark 可执行文件路径。</summary>
    public string ReplaysharkPath
    {
        get => replaysharkPath;
        set => SetFieldAndMarkDirty(ref replaysharkPath, value);
    }

    /// <summary>游戏数据目录。</summary>
    public string ExtractedDataPath
    {
        get => extractedDataPath;
        set => SetFieldAndMarkDirty(ref extractedDataPath, value);
    }

    /// <summary>constants.json 根目录。</summary>
    public string ConstantsRootPath
    {
        get => constantsRootPath;
        set => SetFieldAndMarkDirty(ref constantsRootPath, value);
    }

    /// <summary>ONNX 模型路径，为空时用基线预测器。</summary>
    public string ModelPath
    {
        get => modelPath;
        set => SetFieldAndMarkDirty(ref modelPath, value);
    }

    /// <summary>模型版本标识。</summary>
    public string ModelVersion
    {
        get => modelVersion;
        set => SetFieldAndMarkDirty(ref modelVersion, value);
    }

    /// <summary>WG API 应用 ID。</summary>
    public string WgApplicationId
    {
        get => wgApplicationId;
        set => SetFieldAndMarkDirty(ref wgApplicationId, value);
    }

    /// <summary>是否自动观测回放目录。</summary>
    public bool AutoObserve
    {
        get => autoObserve;
        set => SetFieldAndMarkDirty(ref autoObserve, value);
    }

    /// <summary>观测轮询间隔秒。</summary>
    public int PollIntervalSeconds
    {
        get => pollIntervalSeconds;
        set => SetFieldAndMarkDirty(ref pollIntervalSeconds, value);
    }

    /// <summary>
    /// 主题选择下标：0 跟随系统、1 浅色、2 深色。
    /// </summary>
    /// <remarks>
    /// 修改后立即应用主题，保存时随配置写盘。
    /// </remarks>
    public int ThemeIndex
    {
        get => themeIndex;
        set
        {
            if (SetField(ref themeIndex, value))
            {
                HasUnsavedChanges = true;
                StatusMessage = string.Empty;
                ThemeService.ApplyTheme(ThemeModeFromIndex(value));
            }
        }
    }

    /// <summary>状态提示。</summary>
    public string StatusMessage
    {
        get => statusMessage;
        private set => SetField(ref statusMessage, value);
    }

    /// <summary>是否正在保存。</summary>
    public bool IsBusy
    {
        get => isBusy;
        private set => SetField(ref isBusy, value);
    }

    /// <summary>是否有未保存的改动。</summary>
    public bool HasUnsavedChanges
    {
        get => hasUnsavedChanges;
        private set => SetField(ref hasUnsavedChanges, value);
    }

    /// <summary>选择游戏目录命令。</summary>
    public System.Windows.Input.ICommand BrowseGamePathCommand { get; }

    /// <summary>选择数据库文件命令。</summary>
    public System.Windows.Input.ICommand BrowseDatabasePathCommand { get; }

    /// <summary>选择 replayshark 命令。</summary>
    public System.Windows.Input.ICommand BrowseReplaysharkPathCommand { get; }

    /// <summary>选择游戏数据目录命令。</summary>
    public System.Windows.Input.ICommand BrowseExtractedDataPathCommand { get; }

    /// <summary>选择 constants 根目录命令。</summary>
    public System.Windows.Input.ICommand BrowseConstantsRootPathCommand { get; }

    /// <summary>选择模型文件命令。</summary>
    public System.Windows.Input.ICommand BrowseModelPathCommand { get; }

    /// <summary>保存并应用命令。</summary>
    public System.Windows.Input.ICommand SaveCommand { get; }

    /// <summary>
    /// 校验当前字段。
    /// </summary>
    /// <returns>校验通过返回 null，否则返回错误信息。</returns>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            return "数据库路径不能为空。";
        }

        if (!string.IsNullOrWhiteSpace(GamePath) && !Directory.Exists(GamePath))
        {
            return $"游戏目录不存在: {GamePath}";
        }

        if (!string.IsNullOrWhiteSpace(ReplaysharkPath) && !File.Exists(ReplaysharkPath))
        {
            return $"replayshark 文件不存在: {ReplaysharkPath}";
        }

        if (!string.IsNullOrWhiteSpace(ExtractedDataPath) && !Directory.Exists(ExtractedDataPath))
        {
            return $"游戏数据目录不存在: {ExtractedDataPath}";
        }

        if (!string.IsNullOrWhiteSpace(ConstantsRootPath) && !Directory.Exists(ConstantsRootPath))
        {
            return $"constants 根目录不存在: {ConstantsRootPath}";
        }

        if (!string.IsNullOrWhiteSpace(ModelPath) && !File.Exists(ModelPath))
        {
            return $"模型文件不存在: {ModelPath}";
        }

        if (PollIntervalSeconds < 3)
        {
            return "轮询间隔不能小于 3 秒。";
        }

        return null;
    }

    /// <summary>
    /// 把当前字段写回配置对象。
    /// </summary>
    public AppConfig ToConfig()
    {
        return new AppConfig
        {
            GamePath = GamePath.Trim(),
            DatabasePath = DatabasePath.Trim(),
            ReplaysharkPath = ReplaysharkPath.Trim(),
            ExtractedDataPath = ExtractedDataPath.Trim(),
            ConstantsRootPath = ConstantsRootPath.Trim(),
            ModelPath = ModelPath.Trim(),
            ModelVersion = string.IsNullOrWhiteSpace(ModelVersion) ? "baseline-logistic-v1" : ModelVersion.Trim(),
            WgApplicationId = WgApplicationId.Trim(),
            AutoObserve = AutoObserve,
            PollIntervalSeconds = PollIntervalSeconds,
            Theme = ThemeConfigValue(ThemeIndex),
        };
    }

    /// <summary>
    /// 校验、写盘并应用配置。
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        string? error = Validate();
        if (error is not null)
        {
            StatusMessage = error;
            return;
        }

        IsBusy = true;
        StatusMessage = "正在保存配置...";

        try
        {
            var config = ToConfig();
            config.Save();
            await services.ReconfigureAsync(config, cancellationToken).ConfigureAwait(true);

            HasUnsavedChanges = false;
            StatusMessage = services.PredictionInfo.UsedFallback
                ? $"已保存 · 使用基线预测器: {services.PredictionInfo.Reason}"
                : "已保存 · 已加载模型";
            services.Logs.Info("配置已保存并重新装配服务");
            SettingsApplied?.Invoke();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "保存已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
            services.Logs.Error($"配置保存失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 用配置刷新字段。用于外部重置。
    /// </summary>
    public void LoadFrom(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        gamePath = config.GamePath;
        databasePath = config.DatabasePath;
        replaysharkPath = config.ReplaysharkPath;
        extractedDataPath = config.ExtractedDataPath;
        constantsRootPath = config.ConstantsRootPath;
        modelPath = config.ModelPath;
        modelVersion = config.ModelVersion;
        wgApplicationId = config.WgApplicationId;
        autoObserve = config.AutoObserve;
        pollIntervalSeconds = config.PollIntervalSeconds;
        themeIndex = ThemeIndexFromConfig(config.Theme);
        hasUnsavedChanges = false;

        OnPropertyChanged(nameof(GamePath));
        OnPropertyChanged(nameof(DatabasePath));
        OnPropertyChanged(nameof(ReplaysharkPath));
        OnPropertyChanged(nameof(ExtractedDataPath));
        OnPropertyChanged(nameof(ConstantsRootPath));
        OnPropertyChanged(nameof(ModelPath));
        OnPropertyChanged(nameof(ModelVersion));
        OnPropertyChanged(nameof(WgApplicationId));
        OnPropertyChanged(nameof(AutoObserve));
        OnPropertyChanged(nameof(PollIntervalSeconds));
        OnPropertyChanged(nameof(ThemeIndex));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void BrowseFolder(string title, string current, Action<string> apply)
    {
        string? picked = picker.PickFolder(title, current);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            apply(picked);
        }
    }

    private void BrowseFile(string title, string filter, string current, Action<string> apply)
    {
        string? picked = picker.PickFile(title, filter, current);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            apply(picked);
        }
    }

    private void SetFieldAndMarkDirty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetField(ref field, value, propertyName))
        {
            HasUnsavedChanges = true;
            StatusMessage = string.Empty;
        }
    }

    /// <summary>
    /// 配置字符串转下标。未知值按跟随系统处理。
    /// </summary>
    private static int ThemeIndexFromConfig(string? theme) =>
        ThemeService.ParseMode(theme) switch
        {
            ThemeMode.Light => 1,
            ThemeMode.Dark => 2,
            _ => 0,
        };

    /// <summary>
    /// 下标转主题模式。
    /// </summary>
    private static ThemeMode ThemeModeFromIndex(int index) =>
        index switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

    /// <summary>
    /// 下标转配置字符串。
    /// </summary>
    private static string ThemeConfigValue(int index) =>
        index switch
        {
            1 => "light",
            2 => "dark",
            _ => "system",
        };
}
