using System.Collections.ObjectModel;
using System.IO;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 主窗口视图模型。
/// </summary>
/// <remarks>
/// 承载三块数据：最近对局列表、选中对局的双方名单、预测与真实结果的对照。
/// 预测来自 IPredictor，真实结果来自回放解析，两者在选中对局时合并展示。
/// </remarks>
public sealed class MainViewModel : ViewModelBase
{
    private readonly AppServices services;

    private BattleRecord? selectedBattle;
    private BattleSummaryViewModel? selectedSummary;
    private PredictionResult? currentPrediction;
    private BattleOutcome actualOutcome = BattleOutcome.Unknown;
    private string statusMessage = "就绪";
    private bool isBusy;

    /// <summary>
    /// 构造视图模型。
    /// </summary>
    public MainViewModel(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;

        RefreshCommand = new RelayCommand(
            async _ => await LoadRecentBattlesAsync().ConfigureAwait(true),
            _ => !IsBusy);

        // 降级提示放在状态栏，不静默。
        if (services.PredictionInfo.UsedFallback)
        {
            StatusMessage = $"使用基线预测器: {services.PredictionInfo.Reason}";
        }
    }

    /// <summary>友方名单。</summary>
    public ObservableCollection<PlayerRowViewModel> Allies { get; } = [];

    /// <summary>敌方名单。</summary>
    public ObservableCollection<PlayerRowViewModel> Enemies { get; } = [];

    /// <summary>最近解析出的对局。</summary>
    public ObservableCollection<BattleSummaryViewModel> RecentBattles { get; } = [];

    /// <summary>当前选中的对局。</summary>
    public BattleRecord? SelectedBattle
    {
        get => selectedBattle;
        set
        {
            if (SetField(ref selectedBattle, value))
            {
                OnPropertyChanged(nameof(BattleTitle));
                OnPropertyChanged(nameof(BattleSubtitle));
                _ = LoadBattleDetailsAsync(value);
            }
        }
    }

    /// <summary>
    /// 列表选中的摘要项，设置时同步到底层对局记录。
    /// </summary>
    public BattleSummaryViewModel? SelectedBattleSummary
    {
        get => selectedSummary;
        set
        {
            if (SetField(ref selectedSummary, value))
            {
                SelectedBattle = value?.Battle;
            }
        }
    }

    /// <summary>刷新列表命令。</summary>
    public System.Windows.Input.ICommand RefreshCommand { get; }

    /// <summary>对局标题。</summary>
    public string BattleTitle => selectedBattle is null
        ? "未选择对局"
        : $"{selectedBattle.MapId} · {selectedBattle.BattleType}";

    /// <summary>对局副标题，含时间与结果。</summary>
    public string BattleSubtitle => selectedBattle is null
        ? string.Empty
        : $"{selectedBattle.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · "
          + $"版本 {selectedBattle.GameVersion} · "
          + $"结果 {OutcomeLabel(actualOutcome)}";

    /// <summary>预测的友方胜率，未预测时为 -1。</summary>
    public double PredictedWinrate => currentPrediction?.AllyWinProbability ?? -1;

    /// <summary>预测概率显示文本。</summary>
    public string PredictedWinrateText => currentPrediction is null
        ? "-"
        : currentPrediction.AllyWinProbability.ToString("P1");

    /// <summary>真实结果是否与预测方向一致。</summary>
    public string PredictionVerdict
    {
        get
        {
            if (currentPrediction is null || actualOutcome == BattleOutcome.Unknown)
            {
                return "待对照";
            }

            double? target = actualOutcome.ToBinaryTarget();
            if (target is null)
            {
                return "平局不参与评判";
            }

            bool predictedWin = currentPrediction.AllyWinProbability >= 0.5;
            bool actualWin = target >= 0.5;
            return predictedWin == actualWin ? "预测正确" : "预测错误";
        }
    }

    /// <summary>置信度显示文本。</summary>
    public string ConfidenceText => currentPrediction is null
        ? "-"
        : $"{currentPrediction.Confidence:P0} 数据完整度";

    /// <summary>模型版本显示。</summary>
    public string ModelVersionText => currentPrediction?.ModelVersion ?? services.Predictor.ModelVersion;

    /// <summary>状态栏消息。</summary>
    public string StatusMessage
    {
        get => statusMessage;
        set => SetField(ref statusMessage, value);
    }

    /// <summary>是否正在处理。</summary>
    public bool IsBusy
    {
        get => isBusy;
        set => SetField(ref isBusy, value);
    }

    /// <summary>
    /// 扫描解析产物目录，构建对局列表。
    /// </summary>
    public async Task LoadRecentBattlesAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusMessage = "正在加载对局列表...";

        try
        {
            string parsedDir = ResolveParsedDirectory();
            if (!Directory.Exists(parsedDir))
            {
                StatusMessage = $"解析产物目录不存在: {parsedDir}";
                return;
            }

            var files = Directory.GetFiles(parsedDir, "*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(200)
                .ToList();

            var summaries = new List<BattleSummaryViewModel>();
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var battle = await TryMapAsync(file, cancellationToken).ConfigureAwait(true);
                if (battle is not null)
                {
                    summaries.Add(BattleSummaryViewModel.FromBattle(battle, file));
                }
            }

            RecentBattles.Clear();
            foreach (var s in summaries.OrderByDescending(x => x.StartedAt))
            {
                RecentBattles.Add(s);
            }

            StatusMessage = $"已加载 {RecentBattles.Count} 局";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "加载已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 加载选中对局详情：填名单，跑预测，对照真实结果。
    /// </summary>
    private async Task LoadBattleDetailsAsync(BattleRecord? battle)
    {
        Allies.Clear();
        Enemies.Clear();
        currentPrediction = null;
        actualOutcome = BattleOutcome.Unknown;

        if (battle is null)
        {
            NotifyDetailsChanged();
            return;
        }

        IsBusy = true;
        try
        {
            actualOutcome = battle.Outcome;

            foreach (var p in battle.Allies.OrderByDescending(p => p.AccountWinrate))
            {
                Allies.Add(PlayerRowViewModel.FromPlayer(p));
            }

            foreach (var p in battle.Enemies.OrderByDescending(p => p.AccountWinrate))
            {
                Enemies.Add(PlayerRowViewModel.FromPlayer(p));
            }

            // 跑预测。真实结果此时已知，但预测器不使用它。
            var prediction = await services.Predictor.PredictAsync(battle).ConfigureAwait(true);

            // 回填真实结果用于对照显示。
            var outcome = await services.Repository.GetBattleAsync(battle.BattleId)
                .ConfigureAwait(true);

            currentPrediction = prediction;
            StatusMessage = $"预测完成 · {PredictionVerdict}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"预测失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyDetailsChanged();
        }
    }

    /// <summary>
    /// 触发所有详情相关属性的变更通知。
    /// </summary>
    private void NotifyDetailsChanged()
    {
        OnPropertyChanged(nameof(BattleTitle));
        OnPropertyChanged(nameof(BattleSubtitle));
        OnPropertyChanged(nameof(PredictedWinrate));
        OnPropertyChanged(nameof(PredictedWinrateText));
        OnPropertyChanged(nameof(PredictionVerdict));
        OnPropertyChanged(nameof(ConfidenceText));
        OnPropertyChanged(nameof(ModelVersionText));
    }

    private static string OutcomeLabel(BattleOutcome outcome) => outcome switch
    {
        BattleOutcome.Win => "胜",
        BattleOutcome.Loss => "负",
        BattleOutcome.Draw => "平",
        _ => "未知",
    };

    /// <summary>
    /// 尝试把一份解析产物映射成对局记录。失败返回 null，不抛出。
    /// </summary>
    private static async Task<BattleRecord?> TryMapAsync(
        string file,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var output = Newtonsoft.Json.JsonConvert.DeserializeObject<Replay.Dtos.ReplaysharkOutput>(json);
            if (output is null)
            {
                return null;
            }

            return Replay.ReplaysharkMapper.Map(output, file);
        }
        catch
        {
            // 单份产物损坏不影响整体列表。
            return null;
        }
    }

    private string ResolveParsedDirectory()
    {
        // 与 data/ 目录约定一致：解析产物在 data/parsed。
        string repoData = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "..", "data", "parsed");

        string normalized = Path.GetFullPath(repoData);
        if (Directory.Exists(normalized))
        {
            return normalized;
        }

        // 兜底：配置里若指定了 constants 根目录，取它的同级 parsed。
        string? constantsRoot = Path.GetDirectoryName(services.Config.ConstantsRootPath);
        if (!string.IsNullOrEmpty(constantsRoot))
        {
            string candidate = Path.Combine(constantsRoot, "parsed");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return normalized;
    }
}
