using System.Collections.ObjectModel;
using WowsBattleIntel.Client.Services;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Scoring;
using WowsBattleIntel.Stats;

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

    // 详情加载的代次与取消源。每次选择新对局都递增代次并取消旧的
    // 后台补拉，防止快速连点时旧任务的回写污染新对局的名单。
    private CancellationTokenSource? detailsCts;
    private int detailsVersion;

    // 名单行按账号标识的索引，阶段二增量刷新行内容时定位用。
    private readonly Dictionary<string, PlayerRowViewModel> allyRowsByAccount = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerRowViewModel> enemyRowsByAccount = new(StringComparer.Ordinal);

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
    /// 补拉进度轮询的间隔。补拉按玩家逐个推进，每次到点把已落地的
    /// 数据刷上界面，200 毫秒约对应一到两名玩家的节奏。
    /// </summary>
    private static readonly TimeSpan StatsProgressInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>补拉进度提示的状态栏前缀，换局时据此识别并清掉旧进度。</summary>
    private const string StatsFetchStatusPrefix = "正在补充 ";

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
        Logs = new LogViewModel(services.Logs);
        Settings = new SettingsViewModel(services, pathPicker ?? new NullPathPicker());
        Settings.SettingsApplied += OnSettingsApplied;

        RefreshCommand = new RelayCommand(
            async _ => await LoadRecentBattlesAsync().ConfigureAwait(true),
            _ => !IsBusy);

        RecomputePredictionCommand = new RelayCommand(
            async _ => await RecomputePredictionAsync().ConfigureAwait(true),
            _ => !IsBusy && selectedBattle is not null);

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

    /// <summary>运行日志页签的视图模型。</summary>
    public LogViewModel Logs { get; }

    /// <summary>运行日志埋点通道，取自应用服务装配。与界面侧 Logs 展示模型区分。</summary>
    private LogService LogSink => services.Logs;

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

    /// <summary>重算胜率命令：用当前名单与模型重新生成预测。</summary>
    public System.Windows.Input.ICommand RecomputePredictionCommand { get; }

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
        LogSink.Info("用户开始观测回放目录");
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
        LogSink.Info("用户停止观测");
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
            LogSink.Error($"对局列表加载失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            NotifyListChanged();
        }
    }

    /// <summary>
    /// 加载选中对局详情，渐进式三阶段：阶段一读库加快照合并后立即显示
    /// 名单；阶段二后台补拉缺失统计并逐个刷新行；阶段三收尾重算队伍汇总。
    /// </summary>
    /// <remarks>
    /// 阶段一是仅有的阻塞段，持有 IsBusy；阶段二三在后台推进，不设 IsBusy，
    /// 按钮不闪烁。排序在阶段一固定，依据舰种、等级、账号标识，全部来自
    /// 回放自带数据，与补拉结果无关，阶段二只刷行内容不重排，避免行跳动。
    /// </remarks>
    private async Task LoadBattleDetailsAsync(BattleRecord? battle)
    {
        // 新选择作废旧的后台补拉：取消令牌让拉取尽快退出，代次校验让
        // 已在途的界面回写失效。
        detailsCts?.Cancel();
        detailsCts?.Dispose();
        detailsCts = new CancellationTokenSource();
        detailsVersion++;
        int version = detailsVersion;
        CancellationToken token = detailsCts.Token;

        // 上一局的后台补拉提示还在时清掉，避免新对局显示旧进度。
        if (statusMessage.StartsWith(StatsFetchStatusPrefix, StringComparison.Ordinal))
        {
            StatusMessage = "就绪";
        }

        Allies.Clear();
        Enemies.Clear();
        allyRowsByAccount.Clear();
        enemyRowsByAccount.Clear();
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
        BattleRecord full;
        try
        {
            // 列表查询不加载参战者，详情按标识补一次完整读取。
            full = await services.Repository.GetBattleAsync(battle.BattleId).ConfigureAwait(true)
                ?? battle;

            // battle_players 表不保存战绩字段，账号层统计走快照合并。
            // 展示层取最新快照，不设对局时间上界：给玩家看的是当前战绩，
            // ApeRadar 同口径。预测特征的严格赛前约束在预测管线内部维持。
            full = SnapshotFeatureBuilder.Enrich(full, (accountId, server, shipId) =>
            {
                // 账号层取最新快照；带船 ID 时再取该船最新一条，两层独立命中。
                var account = services.Repository
                    .GetLatestSnapshotAsync(accountId, server)
                    .GetAwaiter()
                    .GetResult();
                if (string.IsNullOrEmpty(shipId))
                {
                    return account;
                }

                return services.Repository
                    .GetLatestShipSnapshotAsync(accountId, server, shipId)
                    .GetAwaiter()
                    .GetResult() ?? account;
            });

            actualOutcome = full.Outcome;

            // 名单排序：真人先于人机（:xxx: 名格式的系统号，不查战绩），
            // 再按舰种分组（航母、战列、巡洋、驱逐、潜艇）、等级高的在前，
            // 最后按账号标识稳定次序。
            foreach (var p in full.Allies
                .OrderBy(p => p.IsBot)
                .ThenBy(p => PlayerRowViewModel.ShipClassSortKey(p.ShipClass))
                .ThenByDescending(p => p.ShipTier)
                .ThenBy(p => p.AccountId, StringComparer.Ordinal))
            {
                var row = PlayerRowViewModel.FromPlayer(p);
                allyRowsByAccount[p.AccountId] = row;
                Allies.Add(row);
            }

            foreach (var p in full.Enemies
                .OrderBy(p => p.IsBot)
                .ThenBy(p => PlayerRowViewModel.ShipClassSortKey(p.ShipClass))
                .ThenByDescending(p => p.ShipTier)
                .ThenBy(p => p.AccountId, StringComparer.Ordinal))
            {
                var row = PlayerRowViewModel.FromPlayer(p);
                enemyRowsByAccount[p.AccountId] = row;
                Enemies.Add(row);
            }

            // 阶段一先给一版汇总，基于快照合并结果；阶段三补拉完成后重算。
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
            LogSink.Error($"详情加载失败: {ex.Message}");
            return;
        }
        finally
        {
            IsBusy = false;
            NotifyDetailsChanged();
        }

        // 阶段二三在后台推进。快照缺失的玩家行此刻显示占位符，
        // 补拉结果落地后增量刷新。
        await FetchMissingStatsInBackgroundAsync(full, version, token).ConfigureAwait(true);
    }

    /// <summary>
    /// 用当前名单与模型重新生成预测并入库。
    /// </summary>
    /// <remarks>
    /// 详情加载展示的是入库时的预测版本。名单经过在线补拉后数据比入库时
    /// 更完整，重算给出基于当前数据的版本，读侧取最新口径自然命中。
    /// </remarks>
    private async Task RecomputePredictionAsync()
    {
        if (selectedBattle is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var battle = await services.Repository.GetBattleAsync(selectedBattle.BattleId).ConfigureAwait(true);
            if (battle is null)
            {
                return;
            }

            // 重算前先把快照合并进来：补拉过的数据不落 battle_players 表，
            // 直接预测会丢掉账号层特征。
            battle = SnapshotFeatureBuilder.Enrich(battle, (accountId, server, shipId) =>
                services.Repository
                    .GetLatestSnapshotAsync(accountId, server)
                    .GetAwaiter()
                    .GetResult());

            var prediction = await services.Predictor.PredictAsync(battle).ConfigureAwait(true);
            await services.Repository.SavePredictionAsync(prediction).ConfigureAwait(true);

            currentPrediction = prediction;

            // 同步刷新左侧列表项的预测概率，否则列表要重启才更新。
            var summary = RecentBattles.FirstOrDefault(s => s.Battle.BattleId == battle.BattleId);
            summary?.UpdatePrediction(prediction.AllyWinProbability);

            StatusMessage = $"已重算 · {prediction.ModelVersion}";
            LogSink.Info($"预测重算完成: {battle.BattleId[..12]} 概率 {prediction.AllyWinProbability:F3}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"重算失败: {ex.Message}";
            LogSink.Error($"预测重算失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            NotifyDetailsChanged();
        }
    }

    /// <summary>
    /// 阶段二：检测缺统计的玩家并在线补拉，拉到即刷新对应行；阶段三：
    /// 全部拉完后落快照、重算行内容与队伍汇总。
    /// </summary>
    /// <remarks>
    /// 存量对局的名单没有战绩字段，快照又缺账号场均或单船数据时，界面
    /// 显示占位符。这里按需在线拉取：账号统计一次批量请求，单船统计逐
    /// 玩家。补拉把统计就地写进玩家记录，轮询把已到的数据增量刷上界面。
    /// 拉取失败不影响展示，保留占位符，用户重试即可。
    /// </remarks>
    private async Task FetchMissingStatsInBackgroundAsync(BattleRecord battle, int version, CancellationToken token)
    {
        var missing = battle.Players
            .Where(p => !p.IsBot && p.AccountId != "-1" && p.AccountId != string.Empty)
            .Where(p => p.AccountBattles < 0 || p.ShipBattles < 0)
            .ToList();

        // 军团分档与统计补拉并行推进，缺统计名单为空时军团查询照常执行。
        var clanTask = FetchClanGradesAsync(battle, version, token);

        if (missing.Count == 0)
        {
            await clanTask.ConfigureAwait(false);
            return;
        }

        string progressText = $"{StatsFetchStatusPrefix}{missing.Count} 名玩家的统计...";
        StatusMessage = progressText;
        LogSink.Info($"快照在线补拉开始，缺统计玩家 {missing.Count} 名");

        try
        {
            var fetchTask = services.StatsProvider.FetchStatsAsync(missing, token);

            // 补拉按玩家逐个推进，轮询到点把已落地的数据刷上界面。
            // 行对象按引用持有，RefreshFrom 只更新内容，不改集合结构。
            while (!fetchTask.IsCompleted)
            {
                await Task.Delay(StatsProgressInterval, token).ConfigureAwait(false);
                Post(() => RefreshRowsFor(version, battle.Players));
            }

            await fetchTask.ConfigureAwait(false);
            LogSink.Info("快照在线补拉完成");
        }
        catch (OperationCanceledException)
        {
            // 已被新选择作废，静默退出。
            await clanTask.ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            // 网络失败或限流，保留占位符，用户重试即可。
            LogSink.Warn($"快照在线补拉失败: {ex.Message}");
            await clanTask.ConfigureAwait(false);
            Post(() =>
            {
                if (IsDetailsCurrent(version) && statusMessage == progressText)
                {
                    StatusMessage = "就绪";
                }
            });
            return;
        }

        // 拉到统计的玩家落快照，单船数据与本局船绑定，下次点开直接命中缓存。
        var capturedAt = DateTimeOffset.UtcNow;
        var snapshots = missing
            .Where(p => p.HasStats)
            .Select(p => PlayerSnapshot.FromPlayer(p, capturedAt))
            .ToList();

        if (snapshots.Count > 0)
        {
            try
            {
                await services.Repository.SaveSnapshotsAsync(snapshots).ConfigureAwait(false);
                LogSink.Info($"快照已落库 {snapshots.Count} 条");

                // 落库顺带按保留天数清一次旧快照，失败静默。
                int deleted = await services.Repository
                    .CleanupSnapshotsAsync(services.Config.SnapshotRetentionDays)
                    .ConfigureAwait(false);
                if (deleted > 0)
                {
                    LogSink.Info($"快照清理完成，删除 {deleted} 条过期记录");
                }
            }
            catch (Exception)
            {
                // 落库失败只影响下次缓存命中，本次展示继续。
            }
        }

        // 阶段三：快照合并后重算。分舰种聚合与单船口径都以合并结果为准，
        // 与阶段一的展示口径完全一致。
        BattleRecord enriched = SnapshotFeatureBuilder.Enrich(battle, (accountId, server, shipId) =>
        {
            // 账号层取最新快照；带船 ID 时再取该船最新一条，两层独立命中。
            var account = services.Repository
                .GetLatestSnapshotAsync(accountId, server)
                .GetAwaiter()
                .GetResult();
            if (string.IsNullOrEmpty(shipId))
            {
                return account;
            }

            return services.Repository
                .GetLatestShipSnapshotAsync(accountId, server, shipId)
                .GetAwaiter()
                .GetResult() ?? account;
        });

        // 军团分档查询与本局玩家统计补拉并行推进，失败静默不影响名单展示。
        await FetchClanGradesAsync(battle, version, token).ConfigureAwait(false);

        Post(() =>
        {
            if (!IsDetailsCurrent(version))
            {
                return;
            }

            RefreshRowsFor(version, enriched.Players);
            AllySummaryText = BuildTeamSummary(enriched.Allies);
            EnemySummaryText = BuildTeamSummary(enriched.Enemies);
            if (statusMessage == progressText)
            {
                StatusMessage = "就绪";
            }
        });
    }

    /// <summary>
    /// 查询本局出现的军团标签并填充分档，完成后刷新行着色与 ToolTip。
    /// </summary>
    /// <remarks>
    /// 军团查询独立于玩家统计：缺统计名单为空时也要查军团，因此单独走一遍。
    /// 失败静默，无分档时标签保持默认色。
    /// </remarks>
    private async Task FetchClanGradesAsync(BattleRecord battle, int version, CancellationToken token)
    {
        var clanCatalog = services.ClanCatalog;
        var playersByTag = new Dictionary<string, List<PlayerRecord>>(StringComparer.Ordinal);
        foreach (var player in battle.Players)
        {
            string tag = ClanCatalogService.NormalizeTag(player.ClanTag);
            if (tag.Length == 0 || player.IsBot)
            {
                continue;
            }

            if (!playersByTag.TryGetValue(tag, out var list))
            {
                list = [];
                playersByTag[tag] = list;
            }

            list.Add(player);
        }

        if (playersByTag.Count == 0)
        {
            return;
        }

        var server = battle.Players[0].Server;
        try
        {
            var grades = await clanCatalog.GetGradesAsync(playersByTag.Keys, server, token).ConfigureAwait(false);
            if (!IsDetailsCurrent(version))
            {
                return;
            }

            Post(() =>
            {
                foreach (var (tag, players) in playersByTag)
                {
                    if (!grades.TryGetValue(tag, out var info))
                    {
                        continue;
                    }

                    foreach (var player in players)
                    {
                        player.ClanColorKey = info.ColorKey;
                    }

                    string detail = BuildClanToolTip(info);
                    foreach (var player in players)
                    {
                        var row = player.IsAlly
                            ? allyRowsByAccount.GetValueOrDefault(player.AccountId)
                            : enemyRowsByAccount.GetValueOrDefault(player.AccountId);
                        row?.SetClanDetail(detail);
                    }
                }

                RefreshRowsFor(version, battle.Players);
            });
        }
        catch (OperationCanceledException)
        {
            // 已被新选择作废，静默退出。
        }
        catch (Exception ex)
        {
            LogSink.Warn($"军团分档查询失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 汇总军团 ToolTip 详情行：军团名、成员数与成员聚合胜率。
    /// </summary>
    internal static string BuildClanToolTip(ClanGradeInfo info)
    {
        string winratePart = info.AggregateWinrate >= 0
            ? info.AggregateWinrate.ToString("P1")
            : "未知";
        string memberPart = info.MembersCount > 0
            ? $"{info.MembersCount} 名成员"
            : "成员数未知";
        return $"{info.Name} | {memberPart} | 聚合胜率 {winratePart}";
    }

    /// <summary>
    /// 按账号标识把最新记录刷进已有名单行，只更新行内容，不改集合结构。
    /// 代次不匹配时直接跳过，旧任务不得回写新对局的名单。
    /// </summary>
    private void RefreshRowsFor(int version, IEnumerable<PlayerRecord> players)
    {
        if (!IsDetailsCurrent(version))
        {
            return;
        }

        foreach (var player in players)
        {
            var row = player.IsAlly
                ? allyRowsByAccount.GetValueOrDefault(player.AccountId)
                : enemyRowsByAccount.GetValueOrDefault(player.AccountId);
            row?.RefreshFrom(player);
        }
    }

    private bool IsDetailsCurrent(int version) => version == detailsVersion;

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
        OnPropertyChanged(nameof(TierText));
        OnPropertyChanged(nameof(TierBrushKey));
        OnPropertyChanged(nameof(PredictionVerdict));
        OnPropertyChanged(nameof(ModelConfidenceText));
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
