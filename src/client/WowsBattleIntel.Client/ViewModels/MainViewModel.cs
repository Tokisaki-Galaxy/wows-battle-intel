using System.Collections.ObjectModel;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 主窗口视图模型。
/// </summary>
/// <remarks>
/// 承载最近对局列表、选中对局的双方名单、预测与真实结果的对照，并持有
/// 校准与配置两个子视图模型。
///
/// 观测事件在后台线程触发，这里用构造时捕获的同步上下文回投到界面线程，
/// 避免视图模型自己依赖 Dispatcher，也便于在无界面的单元测试里运行。
/// </remarks>
public sealed class MainViewModel : ViewModelBase
{
    private readonly AppServices services;
    private readonly SynchronizationContext? syncContext;

    private BattleRecord? selectedBattle;
    private BattleSummaryViewModel? selectedSummary;
    private PredictionResult? currentPrediction;
    private BattleOutcome actualOutcome = BattleOutcome.Unknown;
    private string statusMessage = "就绪";
    private string allySummaryText = "-";
    private string enemySummaryText = "-";
    private bool isBusy;
    private bool isObserving;

    /// <summary>
    /// 构造视图模型。
    /// </summary>
    /// <param name="services">应用服务。</param>
    /// <param name="pathPicker">配置界面的路径选择器。为空时配置项只能手动输入。</param>
    public MainViewModel(AppServices services, IPathPicker? pathPicker = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;

        syncContext = SynchronizationContext.Current;
        Calibration = new CalibrationViewModel(services);
        Settings = new SettingsViewModel(services, pathPicker ?? new NullPathPicker());
        Settings.SettingsApplied += OnSettingsApplied;

        RefreshCommand = new RelayCommand(
            async _ => await LoadRecentBattlesAsync().ConfigureAwait(true),
            _ => !IsBusy);

        ToggleObservationCommand = new RelayCommand(
            _ =>
            {
                ToggleObservation();
                return Task.CompletedTask;
            },
            _ => !IsBusy);

        services.Monitor.BattleIngested += OnBattleIngested;
        services.Monitor.IngestionFailed += OnIngestionFailed;

        UpdatePredictionStatus();
    }

    /// <summary>校准统计子视图模型。</summary>
    public CalibrationViewModel Calibration { get; }

    /// <summary>配置子视图模型。</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>友方名单。</summary>
    public ObservableCollection<PlayerRowViewModel> Allies { get; } = [];

    /// <summary>敌方名单。</summary>
    public ObservableCollection<PlayerRowViewModel> Enemies { get; } = [];

    /// <summary>最近对局，按开始时间倒序。完整列表，分区视图由此派生。</summary>
    public ObservableCollection<BattleSummaryViewModel> RecentBattles { get; } = [];

    /// <summary>实时对局，观测会话内入库的记录，取自 RecentBattles 的前三个小时。</summary>
    public IReadOnlyList<BattleSummaryViewModel> LiveBattles =>
        RecentBattles.Where(s => s.IsLive).ToList();

    /// <summary>历史对局，实时区之外的所有记录。</summary>
    public IReadOnlyList<BattleSummaryViewModel> HistoryBattles =>
        RecentBattles.Where(s => !s.IsLive).ToList();

    /// <summary>是否存在实时对局，用于控制空态提示。</summary>
    public bool HasLiveBattles => RecentBattles.Any(s => s.IsLive);

    /// <summary>一局新回放入库后触发，供视图刷新校准等派生数据。</summary>
    public event Action? BattleListChanged;

    /// <summary>当前选中的对局。</summary>
    public BattleRecord? SelectedBattle
    {
        get => selectedBattle;
        private set
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

    /// <summary>开始或停止实时观测命令。</summary>
    public System.Windows.Input.ICommand ToggleObservationCommand { get; }

    /// <summary>对局标题。</summary>
    public string BattleTitle => selectedBattle is null
        ? "未选择对局"
        : $"{selectedBattle.MapId} · {DisplayLabels.BattleType(selectedBattle.BattleType)}";

    /// <summary>对局副标题，含时间、服务器、时长与结果。</summary>
    public string BattleSubtitle => selectedBattle is null
        ? string.Empty
        : $"{selectedBattle.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · "
          + $"{DisplayLabels.Server(selectedBattle.Server)} · "
          + $"时长 {DisplayLabels.Duration(selectedBattle.DurationSeconds)} · "
          + $"结果 {DisplayLabels.Outcome(actualOutcome)}";

    /// <summary>预测概率显示文本。展示层夹到 [0.05, 0.95]，入库值保持原始。</summary>
    public string PredictedWinrateText => currentPrediction is null
        ? "-"
        : DisplayCalibration.ClampForDisplay(currentPrediction.AllyWinProbability).ToString("P1");

    /// <summary>敌方胜率显示文本，同样夹到展示区间。</summary>
    public string EnemyWinrateText => currentPrediction is null
        ? "-"
        : DisplayCalibration.ClampForDisplay(currentPrediction.EnemyWinProbability).ToString("P1");

    /// <summary>
    /// 友方概率条占比，0 到 1。概率条用两列网格分割，此值决定友方列的宽度。
    /// </summary>
    public double AllyProbabilityRatio => currentPrediction is null
        ? 0.5
        : Math.Clamp(currentPrediction.AllyWinProbability, 0.0, 1.0);

    /// <summary>友方视角的态势分层文案，由展示层概率分档得出。</summary>
    public string TierText => currentPrediction is null
        ? "-"
        : DisplayCalibration.TierText(currentPrediction.AllyWinProbability);

    /// <summary>态势分层文案的强调色资源键。</summary>
    public string TierBrushKey => currentPrediction is null
        ? "TextSecondaryBrush"
        : DisplayCalibration.TierBrushKey(currentPrediction.AllyWinProbability);

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

    /// <summary>置信度显示文本。Confidence 语义为战绩覆盖率，保留旧绑定兼容。</summary>
    public string ConfidenceText => DataCoverageText;

    /// <summary>数据完整度显示文本，即预测所依赖的战绩覆盖率。</summary>
    public string DataCoverageText => currentPrediction is null
        ? "-"
        : $"{currentPrediction.Confidence:P0}";

    /// <summary>
    /// 模型置信度显示文本。由预测概率推导，离五五开越远置信越高。
    /// </summary>
    public string ModelConfidenceText => currentPrediction is null
        ? "-"
        : $"{Math.Abs(currentPrediction.AllyWinProbability - 0.5) * 2:P0}";

    /// <summary>模型版本显示。</summary>
    public string ModelVersionText => currentPrediction?.ModelVersion ?? services.Predictor.ModelVersion;

    /// <summary>预测器装配状态说明。</summary>
    public string PredictorStatusText => services.PredictionInfo.UsedFallback
        ? $"基线预测器 · {services.PredictionInfo.Reason}"
        : $"模型推理 · {services.Predictor.ModelVersion}";

    /// <summary>状态栏消息。</summary>
    public string StatusMessage
    {
        get => statusMessage;
        private set => SetField(ref statusMessage, value);
    }

    /// <summary>友方队伍汇总，平均胜率与平均伤害，参照 PotatoAlert 底部汇总条。</summary>
    public string AllySummaryText
    {
        get => allySummaryText;
        private set => SetField(ref allySummaryText, value);
    }

    /// <summary>敌方队伍汇总，平均胜率与平均伤害。</summary>
    public string EnemySummaryText
    {
        get => enemySummaryText;
        private set => SetField(ref enemySummaryText, value);
    }

    /// <summary>是否正在处理。</summary>
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                // 进度反馈统一走顶部状态文本，不再单设状态栏。
                OnPropertyChanged(nameof(ObservationStatusText));
            }
        }
    }

    /// <summary>是否正在实时观测。</summary>
    public bool IsObserving
    {
        get => isObserving;
        private set
        {
            if (SetField(ref isObserving, value))
            {
                OnPropertyChanged(nameof(ObservationButtonText));
                OnPropertyChanged(nameof(ObservationStatusText));
            }
        }
    }

    /// <summary>观测按钮文本。</summary>
    public string ObservationButtonText => isObserving ? "停止观测" : "开始观测";

    /// <summary>观测状态说明。忙碌时显示加载中，空闲时显示观测状态。</summary>
    public string ObservationStatusText => isBusy
        ? "加载中..."
        : isObserving
            ? $"观测中 · 待处理 {services.Monitor.PendingCount}"
            : "未观测";

    /// <summary>
    /// 启动实时观测。
    /// </summary>
    public void StartObservation()
    {
        if (isObserving)
        {
            return;
        }

        services.StartObservation();
        IsObserving = true;
        StatusMessage = "已开始观测回放目录";
    }

    /// <summary>
    /// 停止实时观测。
    /// </summary>
    public void StopObservation()
    {
        if (!isObserving)
        {
            return;
        }

        services.StopObservation();
        IsObserving = false;
        StatusMessage = "已停止观测";
    }

    /// <summary>
    /// 刷新观测状态显示。待处理数量由后台协程更新，界面需要主动拉取。
    /// </summary>
    public void RefreshObservationStatus() => OnPropertyChanged(nameof(ObservationStatusText));

    /// <summary>
    /// 从本地库读取最近对局并填充列表。
    /// </summary>
    public async Task LoadRecentBattlesAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusMessage = "正在加载对局列表...";

        try
        {
            var battles = await services.Repository
                .GetRecentBattlesAsync(limit: 300, cancellationToken)
                .ConfigureAwait(true);

            // 列表查询不带参战者，批量补齐，列表项的等级标签需要名单。
            await services.Repository
                .AttachPlayersAsync(battles, cancellationToken)
                .ConfigureAwait(true);

            // 逐局取预测胜率，列表上直接显示对照所需的关键数字。
            var summaries = new List<BattleSummaryViewModel>(battles.Count);
            foreach (var battle in battles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prediction = await services.Repository
                    .GetPredictionAsync(battle.BattleId, cancellationToken)
                    .ConfigureAwait(true);

                summaries.Add(BattleSummaryViewModel.FromBattle(
                    battle,
                    sourcePath: string.Empty,
                    predictedWinProbability: prediction?.AllyWinProbability));
            }

            string? keepId = selectedBattle?.BattleId;

            RecentBattles.Clear();
            foreach (var summary in summaries)
            {
                RecentBattles.Add(summary);
            }

            // 尽量保持原选中项，刷新后不跳走。
            if (keepId is not null)
            {
                var reselect = RecentBattles.FirstOrDefault(s => s.Battle.BattleId == keepId);
                if (reselect is not null)
                {
                    SelectedBattleSummary = reselect;
                }
            }
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
            NotifyListChanged();
        }
    }

    /// <summary>
    /// 加载选中对局详情：填名单，读取该局预测，对照真实结果。
    /// </summary>
    private async Task LoadBattleDetailsAsync(BattleRecord? battle)
    {
        Allies.Clear();
        Enemies.Clear();
        currentPrediction = null;
        actualOutcome = BattleOutcome.Unknown;
        AllySummaryText = "-";
        EnemySummaryText = "-";

        if (battle is null)
        {
            NotifyDetailsChanged();
            return;
        }

        IsBusy = true;
        try
        {
            // 列表查询不加载参战者，详情按标识补一次完整读取。
            var full = await services.Repository.GetBattleAsync(battle.BattleId).ConfigureAwait(true)
                ?? battle;

            // battle_players 表不保存战绩字段，账号层统计走快照合并。
            // 展示层取最新快照，不设对局时间上界：给玩家看的是当前战绩，
            // ApeRadar 同口径。预测特征的严格赛前约束在预测管线内部维持。
            full = SnapshotFeatureBuilder.Enrich(full, (accountId, server) =>
                services.Repository
                    .GetLatestSnapshotAsync(accountId, server)
                    .GetAwaiter()
                    .GetResult());

            // 名单里还有玩家缺统计时在线补拉一次并落快照，下次点开直接命中缓存。
            full = await FetchMissingStatsAsync(full).ConfigureAwait(true);

            actualOutcome = full.Outcome;

            // 名单按舰种分组排序：航母、战列、巡洋、驱逐、潜艇，同舰种内按账号标识稳定次序。
            foreach (var p in full.Allies
                .OrderBy(p => PlayerRowViewModel.ShipClassSortKey(p.ShipClass))
                .ThenBy(p => p.AccountId, StringComparer.Ordinal))
            {
                Allies.Add(PlayerRowViewModel.FromPlayer(p));
            }

            foreach (var p in full.Enemies
                .OrderBy(p => PlayerRowViewModel.ShipClassSortKey(p.ShipClass))
                .ThenBy(p => p.AccountId, StringComparer.Ordinal))
            {
                Enemies.Add(PlayerRowViewModel.FromPlayer(p));
            }

            AllySummaryText = BuildTeamSummary(full.Allies);
            EnemySummaryText = BuildTeamSummary(full.Enemies);

            // 预测取自已入库记录。入库时算过，这里不重算，保证展示的是当时那一版。
            currentPrediction = await services.Repository
                .GetPredictionAsync(full.BattleId)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载详情失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyDetailsChanged();
        }
    }

    /// <summary>
    /// 对缺统计的玩家在线补拉一次，结果落快照缓存。
    /// </summary>
    /// <remarks>
    /// 存量对局的名单没有战绩字段，快照又缺账号场均或单船数据时，界面会
    /// 整列显示占位符。这里选点开的那一局按需拉取：账号统计一次批量请求，
    /// 单船统计逐玩家。拉到的写回快照表，下次点开同一局直接命中缓存。
    /// 拉取失败不影响展示，保留占位符。
    /// </remarks>
    private async Task<BattleRecord> FetchMissingStatsAsync(BattleRecord battle)
    {
        var missing = battle.Players
            .Where(p => !p.IsBot && p.AccountId != "-1" && p.AccountId != string.Empty)
            .Where(p => p.AccountBattles < 0 || p.ShipBattles < 0)
            .ToList();

        if (missing.Count == 0)
        {
            return battle;
        }

        try
        {
            await services.StatsProvider.FetchStatsAsync(missing).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 网络失败或限流，保留占位符，用户重试即可。
            return battle;
        }

        // 拉到统计的玩家落快照，单船数据与本局船绑定。
        var capturedAt = DateTimeOffset.UtcNow;
        var snapshots = missing
            .Where(p => p.HasStats)
            .Select(p => PlayerSnapshot.FromPlayer(p, capturedAt))
            .ToList();

        if (snapshots.Count > 0)
        {
            await services.Repository
                .SaveSnapshotsAsync(snapshots)
                .ConfigureAwait(true);

            // 用新数据重排：快照合并后重新生成名单行。
            return SnapshotFeatureBuilder.Enrich(battle, (accountId, server) =>
                services.Repository
                    .GetLatestSnapshotAsync(accountId, server)
                    .GetAwaiter()
                    .GetResult());
        }

        return battle;
    }

    /// <summary>
    /// 计算队伍汇总文本，ApeRadar 口径：平均数据一行给出账号胜率、加权胜率与平均场次。
    /// 只统计有对应数据的玩家，三项都无有效样本时显示 "-"。
    /// 内部静态以便测试直接验证口径。
    /// </summary>
    internal static string BuildTeamSummary(IEnumerable<PlayerRecord> players)
    {
        var list = players.ToList();
        var withWinrate = list.Where(p => p.AccountWinrate >= 0).ToList();
        var withWeighted = list.Where(p => p.WeightedWinrate >= 0).ToList();
        var withBattles = list.Where(p => p.AccountBattles >= 0).ToList();

        if (withWinrate.Count == 0 && withWeighted.Count == 0 && withBattles.Count == 0)
        {
            return "-";
        }

        string winratePart = withWinrate.Count == 0
            ? "-"
            : withWinrate.Average(p => p.AccountWinrate).ToString("P2");
        string weightedPart = withWeighted.Count == 0
            ? "-"
            : withWeighted.Average(p => p.WeightedWinrate).ToString("P2");
        string battlesPart = withBattles.Count == 0
            ? "-"
            : withBattles.Average(p => p.AccountBattles).ToString("F1");

        return $"账号: {winratePart}  加权: {weightedPart}  场次: {battlesPart}";
    }

    private void ToggleObservation()
    {
        if (isObserving)
        {
            StopObservation();
        }
        else
        {
            StartObservation();
        }
    }

    private void OnBattleIngested(IngestionResult result)
    {
        Post(() =>
        {
            string name = result.Battle?.MapId ?? "新对局";
            string suffix = result.Message is null ? string.Empty : $"（{result.Message}）";
            StatusMessage = result.IsDuplicate
                ? $"已跳过 {name}{suffix}"
                : $"已入库 {name}{suffix}";
            OnPropertyChanged(nameof(ObservationStatusText));
            _ = RefreshAfterIngestionAsync();
        });
    }

    private void OnIngestionFailed(string replayPath, string message)
    {
        Post(() =>
        {
            StatusMessage = $"回放处理失败: {message}";
            OnPropertyChanged(nameof(ObservationStatusText));
        });
    }

    private async Task RefreshAfterIngestionAsync()
    {
        await LoadRecentBattlesAsync().ConfigureAwait(true);
        BattleListChanged?.Invoke();
    }

    private void OnSettingsApplied()
    {
        Post(() =>
        {
            OnPropertyChanged(nameof(ModelVersionText));
            OnPropertyChanged(nameof(PredictorStatusText));
            _ = RefreshAfterIngestionAsync();
        });
    }

    /// <summary>
    /// 把动作回投到界面线程。无同步上下文时直接执行，便于测试。
    /// </summary>
    private void Post(Action action)
    {
        if (syncContext is null)
        {
            action();
        }
        else
        {
            syncContext.Post(_ => action(), null);
        }
    }

    private void UpdatePredictionStatus()
    {
        if (services.PredictionInfo.UsedFallback)
        {
            // 降级提示放在状态栏，不静默。
            StatusMessage = $"使用基线预测器: {services.PredictionInfo.Reason}";
        }
    }

    /// <summary>
    /// 触发所有详情相关属性的变更通知。
    /// </summary>
    private void NotifyDetailsChanged()
    {
        OnPropertyChanged(nameof(BattleTitle));
        OnPropertyChanged(nameof(BattleSubtitle));
        OnPropertyChanged(nameof(PredictedWinrateText));
        OnPropertyChanged(nameof(EnemyWinrateText));
        OnPropertyChanged(nameof(AllyProbabilityRatio));
        OnPropertyChanged(nameof(TierText));
        OnPropertyChanged(nameof(TierBrushKey));
        OnPropertyChanged(nameof(PredictionVerdict));
        OnPropertyChanged(nameof(DataCoverageText));
        OnPropertyChanged(nameof(ModelConfidenceText));
        OnPropertyChanged(nameof(ConfidenceText));
        OnPropertyChanged(nameof(ModelVersionText));
    }

    /// <summary>
    /// 触发列表分区相关属性的变更通知。列表重建后派生集合需要重新求值。
    /// </summary>
    private void NotifyListChanged()
    {
        OnPropertyChanged(nameof(LiveBattles));
        OnPropertyChanged(nameof(HistoryBattles));
        OnPropertyChanged(nameof(HasLiveBattles));
    }

    /// <summary>
    /// 无界面环境下的空路径选择器，始终返回取消。
    /// </summary>
    private sealed class NullPathPicker : IPathPicker
    {
        public string? PickFolder(string title, string currentPath) => null;

        public string? PickFile(string title, string filter, string currentPath) => null;
    }
}
