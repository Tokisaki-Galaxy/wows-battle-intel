using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Scoring;

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
    private string modelPath = string.Empty;
    private string modelVersion = string.Empty;
    private string wgApplicationId = string.Empty;
    private int pollIntervalSeconds = 10;
    private int snapshotRetentionDays = 14;
    private bool enableBattleCache;
    private int battleCacheDays = 7;
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
        DetectGamePathCommand = SyncCommand(_ => DetectGamePath());
        BrowseDatabasePathCommand = SyncCommand(_ => BrowseFile(
            "选择数据库文件", "SQLite 数据库 (*.db)|*.db|所有文件 (*.*)|*.*", DatabasePath,
            p => DatabasePath = p));
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

    /// <summary>观测轮询间隔秒。</summary>
    public int PollIntervalSeconds
    {
        get => pollIntervalSeconds;
        set => SetFieldAndMarkDirty(ref pollIntervalSeconds, value);
    }

    /// <summary>快照保留天数，0 表示不清理。</summary>
    public int SnapshotRetentionDays
    {
        get => snapshotRetentionDays;
        set => SetFieldAndMarkDirty(ref snapshotRetentionDays, value);
    }

    /// <summary>
    /// 是否缓存对局战绩加速查询。开启后同一玩家在有效期内重复查询直接命中本地。
    /// </summary>
    public bool EnableBattleCache
    {
        get => enableBattleCache;
        set => SetFieldAndMarkDirty(ref enableBattleCache, value);
    }

    /// <summary>
    /// 战绩缓存有效期天数，默认 7。0 表示不设新鲜度上界，永不按时间过期。
    /// </summary>
    /// <remarks>
    /// 与缓存开关联动：关闭缓存时该项在界面上禁用，但仍按原值保存，
    /// 用户重新开启后沿用上次设置的时长。
    /// </remarks>
    public int BattleCacheDays
    {
        get => battleCacheDays;
        set => SetFieldAndMarkDirty(ref battleCacheDays, value);
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

    /// <summary>
    /// 名单染色规则编辑项。前两条分别是紫色与红色规则，顺序即优先级。
    /// </summary>
    /// <remarks>
    /// 固定两条：紫规则在前保证高胜率优先判紫。用户只改指标、算子与阈值，
    /// 不改颜色。编辑后标记脏，保存时随配置写盘。
    /// </remarks>
    public ObservableCollection<ColorRuleViewModel> ColorRules { get; } = [];

    /// <summary>
    /// 状态提示。
    /// </summary>
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

    /// <summary>自动探测游戏目录命令。</summary>
    public System.Windows.Input.ICommand DetectGamePathCommand { get; }

    /// <summary>选择数据库文件命令。</summary>
    public System.Windows.Input.ICommand BrowseDatabasePathCommand { get; }

    /// <summary>选择模型文件命令。</summary>
    public System.Windows.Input.ICommand BrowseModelPathCommand { get; }

    /// <summary>保存并应用命令。</summary>
    public System.Windows.Input.ICommand SaveCommand { get; }

    /// <summary>
    /// 程序集版本号，用于设置页落款展示，如 v0.1.0。
    /// </summary>
    /// <remarks>
    /// 版本号来自 Directory.Build.props 的 Version 属性，经 InformationalVersion 落进程序集。
    /// 该值带源码修订后缀时截断到数字段，让界面上只出现可读的语义版本。
    /// </remarks>
    public string VersionText
    {
        get
        {
            string raw = typeof(SettingsViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? typeof(SettingsViewModel).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";

            int plus = raw.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0)
            {
                raw = raw[..plus];
            }

            return $"v{raw}";
        }
    }

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

        if (!string.IsNullOrWhiteSpace(ModelPath) && !File.Exists(ModelPath))
        {
            return $"模型文件不存在: {ModelPath}";
        }

        if (PollIntervalSeconds < 3)
        {
            return "轮询间隔不能小于 3 秒。";
        }

        if (SnapshotRetentionDays < 0)
        {
            return "快照保留天数不能为负，0 表示不清理。";
        }

        if (BattleCacheDays < 0)
        {
            return "战绩缓存天数不能为负，0 表示不过期。";
        }

        foreach (var rule in ColorRules)
        {
            if (rule.Threshold < 0)
            {
                return $"染色规则「{rule.ColorLabel}」的阈值不能为负。";
            }

            if (ColorRuleViewModel.IsWinrateMetric(rule.Metric) && rule.Threshold > 100)
            {
                return $"染色规则「{rule.ColorLabel}」的胜率阈值应在 0 到 100 之间。";
            }
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
            ModelPath = ModelPath.Trim(),
            ModelVersion = string.IsNullOrWhiteSpace(ModelVersion) ? "baseline-logistic-v1" : ModelVersion.Trim(),
            WgApplicationId = WgApplicationId.Trim(),
            PollIntervalSeconds = PollIntervalSeconds,
            SnapshotRetentionDays = SnapshotRetentionDays,
            EnableBattleCache = EnableBattleCache,
            BattleCacheDays = BattleCacheDays,
            ColorRules = ColorRules.Select(r => r.ToRule()).ToList(),
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
        modelPath = config.ModelPath;
        modelVersion = config.ModelVersion;
        wgApplicationId = config.WgApplicationId;
        pollIntervalSeconds = config.PollIntervalSeconds;
        snapshotRetentionDays = config.SnapshotRetentionDays;
        enableBattleCache = config.EnableBattleCache;
        battleCacheDays = config.BattleCacheDays;
        themeIndex = ThemeIndexFromConfig(config.Theme);
        hasUnsavedChanges = false;

        OnPropertyChanged(nameof(GamePath));
        OnPropertyChanged(nameof(DatabasePath));
        OnPropertyChanged(nameof(ModelPath));
        OnPropertyChanged(nameof(ModelVersion));
        OnPropertyChanged(nameof(WgApplicationId));
        OnPropertyChanged(nameof(PollIntervalSeconds));
        OnPropertyChanged(nameof(SnapshotRetentionDays));
        OnPropertyChanged(nameof(EnableBattleCache));
        OnPropertyChanged(nameof(BattleCacheDays));
        OnPropertyChanged(nameof(ThemeIndex));
        OnPropertyChanged(nameof(HasUnsavedChanges));

        LoadColorRules(config.ColorRules);
    }

    private void BrowseFolder(string title, string current, Action<string> apply)
    {
        string? picked = picker.PickFolder(title, current);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            apply(picked);
        }
    }

    /// <summary>
    /// 自动探测游戏目录：先看运行中的游戏进程，再翻最近使用记录。
    /// </summary>
    private void DetectGamePath()
    {
        var found = GamePathDetector.Detect();
        if (found is null)
        {
            StatusMessage = "未探测到游戏目录。请先开一局游戏再点探测，或手动浏览选择。";
            return;
        }

        GamePath = found.GamePath;
        StatusMessage = $"已探测到游戏目录（{found.Source}），记得保存。";
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
    /// 用配置里的染色规则重建编辑项。固定两条，缺位补默认。
    /// </summary>
    /// <remarks>
    /// 颜色由序位决定：第一条紫、第二条红。配置里键名缺失或顺序错乱时按序位
    /// 强制校正，保证界面上的"紫色/红色"标签与实际染色一致。
    /// </remarks>
    private void LoadColorRules(IReadOnlyList<ColorRule>? source)
    {
        foreach (var existing in ColorRules)
        {
            existing.PropertyChanged -= OnColorRuleChanged;
        }

        ColorRules.Clear();

        var fallback = DefaultColorRules.Value;
        var labels = new[] { "紫色", "红色" };
        var keys = new[] { "RowUnicumBrush", "RowApeBrush" };

        for (int i = 0; i < labels.Length; i++)
        {
            ColorRule rule = source is not null && i < source.Count
                ? source[i]
                : fallback[i];

            // 键名按序位校正，防止配置被外部改乱后标签与颜色对不上。
            var normalized = new ColorRule
            {
                Metric = rule.Metric,
                Operator = rule.Operator,
                Threshold = rule.Threshold,
                RowBrushKey = keys[i],
            };

            var vm = new ColorRuleViewModel(normalized, labels[i]);
            vm.PropertyChanged += OnColorRuleChanged;
            ColorRules.Add(vm);
        }
    }

    /// <summary>任一条染色规则被编辑即标记脏。</summary>
    private void OnColorRuleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        HasUnsavedChanges = true;
        StatusMessage = string.Empty;
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
