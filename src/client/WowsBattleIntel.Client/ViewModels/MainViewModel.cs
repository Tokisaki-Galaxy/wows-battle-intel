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

    // 历史对局筛选条件。null 表示不限。
    private BattleType? historyBattleTypeFilter;
    private int? historyDaysFilter;

    // 名单行按玩家身份键的索引，阶段二增量刷新行内容时定位用。
    // 键取昵称：赛前名单的账号 ID 先是 -1 哨兵，补拉阶段才解析成真账号，
    // 用账号 ID 建索引会全体撞键；昵称在补拉与快照合并前后都保持不变。
    private readonly Dictionary<string, PlayerRowViewModel> allyRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerRowViewModel> enemyRows = new(StringComparer.Ordinal);

    private BattleRecord? selectedBattle;
    private BattleSummaryViewModel? selectedLiveBattle;
    private BattleSummaryViewModel? selectedHistoryBattle;
    private BattleSummaryViewModel? currentBattle;
    private PredictionResult? currentPrediction;
    private BattleOutcome actualOutcome = BattleOutcome.Unknown;
    private string statusMessage = "就绪";
    private string allySummaryText = "-";
    private string enemySummaryText = "-";    private bool isBusy;
    private string countdownText = string.Empty;

    // 赛前链路在途状态：临时名单触发过展示后为 true，正式回放入库后复位。
    // 防止同一局的 tempArenaInfo 在轮询窗口内反复触发整条链路。
    private string? preBattleKey;

    // 赛前数据拉取进度文案，嵌进倒计时行显示。
    private string preBattleProgressText = string.Empty;

    // 赛前收敛循环的取消源与最近一版预测。循环独立于详情区选中：切到历史
    // 回放再切回来时，详情区重建不得把已算出的预测与在跑的拉取抹掉。
    private CancellationTokenSource? preBattleCts;
    private PredictionResult? preBattlePrediction;

    // 赛前收敛是否以失败告终（连续多轮无进展），用于倒计时红色提醒。
    private bool preBattleFailed;

    /// <summary>赛前收敛连续无进展的轮数上限，达到即停止并红色提醒。</summary>
    private const int PreBattleMaxConsecutiveMisses = 3;

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

        RecomputePredictionCommand = new RelayCommand(
            async _ => await RecomputePredictionAsync().ConfigureAwait(true),
            _ => !IsBusy && selectedBattle is not null);

        services.Monitor.BattleIngested += OnBattleIngested;
        services.Monitor.IngestionFailed += OnIngestionFailed;
        services.ArenaWatcher.PreBattleDiscovered += OnPreBattleDiscovered;
        services.ArenaWatcher.PreBattleEnded += OnPreBattleEnded;

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

    /// <summary>
    /// 当前正在进行的对局。来源是 tempArenaInfo.json，对局结束后清空。
    /// </summary>
    /// <remarks>
    /// 单独持有而非从 RecentBattles 派生：正式回放入库触发的列表重建会
    /// 清空并重填 RecentBattles，派生方案会把正在进行的对局一并抹掉。
    /// </remarks>
    public IReadOnlyList<BattleSummaryViewModel> LiveBattles =>
        currentBattle is null ? [] : [currentBattle];

    /// <summary>历史对局，符合筛选条件的入库记录。</summary>
    public IReadOnlyList<BattleSummaryViewModel> HistoryBattles =>
        RecentBattles
            .Where(s => HistoryBattleTypeFilter is null || s.Battle.BattleType == HistoryBattleTypeFilter)
            .Where(s => HistoryDaysFilter is null
                || s.StartedAt >= DateTimeOffset.UtcNow.AddDays(-HistoryDaysFilter.Value))
            .ToList();

    /// <summary>是否存在正在进行的对局，用于控制空态提示。</summary>
    public bool HasLiveBattles => currentBattle is not null;

    /// <summary>历史对局的模式筛选。null 表示全部模式。</summary>
    public BattleType? HistoryBattleTypeFilter
    {
        get => historyBattleTypeFilter;
        set
        {
            if (SetField(ref historyBattleTypeFilter, value))
            {
                OnPropertyChanged(nameof(HistoryBattles));
            }
        }
    }

    /// <summary>历史对局的日期筛选天数。null 表示不限，例如 7 表示近七天。</summary>
    public int? HistoryDaysFilter
    {
        get => historyDaysFilter;
        set
        {
            if (SetField(ref historyDaysFilter, value))
            {
                OnPropertyChanged(nameof(HistoryBattles));
            }
        }
    }

    /// <summary>历史模式筛选的下拉索引。0 全部，1 随机，2 排位，3 联合作战，4 其他（非上述模式）。</summary>
    public int HistoryBattleTypeFilterIndex
    {
        get => historyBattleTypeFilter switch
        {
            BattleType.Random => 1,
            BattleType.Ranked => 2,
            BattleType.Coop => 3,
            _ => historyBattleTypeFilter.HasValue ? 4 : 0,
        };
        set => HistoryBattleTypeFilter = value switch
        {
            1 => BattleType.Random,
            2 => BattleType.Ranked,
            3 => BattleType.Coop,
            4 => BattleType.Unknown,
            _ => null,
        };
    }

    /// <summary>历史日期筛选的下拉索引。0 为全部，1 近一天，2 近七天，3 近三十天。</summary>
    public int HistoryDaysFilterIndex
    {
        get => historyDaysFilter switch
        {
            1 => 1,
            7 => 2,
            30 => 3,
            _ => 0,
        };
        set => HistoryDaysFilter = value switch
        {
            1 => 1,
            2 => 7,
            3 => 30,
            _ => null,
        };
    }

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
                // 倒计时只在详情区显示当前赛前对局时可见，切换选中要重算。
                OnPropertyChanged(nameof(CountdownText));
                OnPropertyChanged(nameof(CountdownBrushKey));
                _ = LoadBattleDetailsAsync(value);
            }
        }
    }

    /// <summary>
    /// 当前对局列表选中的摘要项，设置时同步到底层对局记录。
    /// </summary>
    /// <remarks>
    /// 实时区与历史区各持一个选中项，不能共用。两个 ListBox 若绑定同一个
    /// SelectedItem，在其中一个列表里选中时，另一个会把该项置空并回写 null，
    /// 表现为"点了历史回放就再也点不回当前对局"。
    /// </remarks>
    public BattleSummaryViewModel? SelectedLiveBattle
    {
        get => selectedLiveBattle;
        set
        {
            if (SetField(ref selectedLiveBattle, value))
            {
                if (value is not null)
                {
                    // 两个列表互斥选中：选实时区时清掉历史区的选中。
                    SetField(ref selectedHistoryBattle, null, nameof(SelectedHistoryBattle));
                    SelectedBattle = value.Battle;
                }
            }
        }
    }

    /// <summary>历史对局列表选中的摘要项，设置时同步到底层对局记录。</summary>
    public BattleSummaryViewModel? SelectedHistoryBattle
    {
        get => selectedHistoryBattle;
        set
        {
            if (SetField(ref selectedHistoryBattle, value))
            {
                if (value is not null)
                {
                    // 两个列表互斥选中：选历史区时清掉实时区的选中。
                    SetField(ref selectedLiveBattle, null, nameof(SelectedLiveBattle));
                    SelectedBattle = value.Battle;
                }
            }
        }
    }

    /// <summary>重算胜率命令：用当前名单与模型重新生成预测。</summary>
    public System.Windows.Input.ICommand RecomputePredictionCommand { get; }

    /// <summary>对局标题。</summary>
    public string BattleTitle => selectedBattle is null
        ? "未选择对局"
        : selectedBattle.MapId.Length == 0
            ? $"赛前名单 · {DisplayLabels.BattleType(selectedBattle.BattleType)}"
            : $"{selectedBattle.MapId} · {DisplayLabels.BattleType(selectedBattle.BattleType)}";

    /// <summary>对局副标题，含时间、服务器、时长与结果。赛前态不显示时长与结果。</summary>
    public string BattleSubtitle => selectedBattle is null
        ? string.Empty
        : selectedBattle.MapId.Length == 0
            ? $"{selectedBattle.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · "
              + $"{DisplayLabels.Server(selectedBattle.Server)} · 赛前预测"
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

    /// <summary>
    /// 赛前数据刷新倒计时，空串表示当前没有更新循环在跑。
    /// </summary>
    /// <remarks>
    /// 原始文案由赛前收敛循环写进 countdownText。对外只在详情区正显示这一局
    /// 赛前对局时才给出，切到历史回放时返回空串，避免把赛前的"补拉受阻"之类
    /// 提示挂在无关的历史对局上。
    /// </remarks>
    public string CountdownText => IsViewingPreBattle ? countdownText : string.Empty;

    /// <summary>详情区当前显示的是否就是正在观测的赛前对局。</summary>
    private bool IsViewingPreBattle =>
        currentBattle is not null && ReferenceEquals(selectedBattle, currentBattle.Battle);

    /// <summary>
    /// 写入倒计时原始文案并通知对外属性。
    /// </summary>
    /// <remarks>
    /// 对外 CountdownText 会按当前选中过滤，这里只更新底层字段，通知由
    /// 属性名固定触发，界面据此重新求值过滤结果。
    /// </remarks>
    private void SetCountdownText(string value)
    {
        if (SetField(ref countdownText, value, nameof(CountdownText)))
        {
            // 过滤结果与原始值都随本属性变化，颜色键一并刷新。
            OnPropertyChanged(nameof(CountdownBrushKey));
        }
    }

    /// <summary>
    /// 倒计时文本的颜色资源键。收敛失败时用危险色提醒，否则主色。
    /// </summary>
    public string CountdownBrushKey => preBattleFailed ? "DangerBrush" : "PrimaryFgBrush";

    /// <summary>模型版本显示。</summary>
    public string ModelVersionText => currentPrediction?.ModelVersion ?? services.Predictor.ModelVersion;

    /// <summary>预测器装配状态说明。</summary>
    public string PredictorStatusText => services.PredictionInfo.UsedFallback
        ? $"基线预测器 · {services.PredictionInfo.Reason}"
        : services.Predictor.ModelVersion;

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

    /// <summary>观测状态说明。观测常驻，展示是否忙碌与待处理数量。</summary>
    public string ObservationStatusText => isBusy
        ? "加载中..."
        : $"观测中 · 待处理 {services.Monitor.PendingCount}";

    /// <summary>
    /// 收起当前对局。来源是 tempArenaInfo 的实时态，配置换绑后不再有后续
    /// 事件驱动它，主动清空以免界面停在过期的一局上。
    /// </summary>
    private void ClearCurrentBattle()
    {
        if (currentBattle is null)
        {
            return;
        }

        // 停掉收敛循环并清空它持有的预测，避免残留任务继续拉取或回写。
        preBattleCts?.Cancel();
        preBattleCts?.Dispose();
        preBattleCts = null;
        preBattlePrediction = null;
        preBattleFailed = false;

        currentBattle = null;
        preBattleKey = null;
        SetCountdownText(string.Empty);
        OnPropertyChanged(nameof(LiveBattles));
        OnPropertyChanged(nameof(HasLiveBattles));
    }

    /// <summary>
    /// 刷新观测状态显示。待处理数量由后台协程更新，界面需要主动拉取。
    /// </summary>
    public void RefreshObservationStatus() => OnPropertyChanged(nameof(ObservationStatusText));

    /// <summary>
    /// 切回对局情报页时调用：按当前选中重刷一次详情。
    /// </summary>
    /// <remarks>
    /// 收敛循环在后台独立推进，切到运行日志或设置页期间它照跑，但界面被盖住
    /// 期间用户看不到变化。切回来时按最新状态重建一次详情，名单、倒计时与队伍
    /// 汇总都对齐到后台的最新进度，不停留在切走前的旧文字。
    ///
    /// 选中为空时不做事；赛前对局在重建时用收敛循环持有的预测，不会被清空。
    /// </remarks>
    public Task RefreshIntelViewAsync() => LoadBattleDetailsAsync(selectedBattle);

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

            // 尽量保持原选中项，刷新后不跳走。实时区与历史区各恢复各自的选中。
            if (keepId is not null)
            {
                var reselect = RecentBattles.FirstOrDefault(s => s.Battle.BattleId == keepId);
                if (reselect is not null)
                {
                    SelectedHistoryBattle = reselect;
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
        allyRows.Clear();
        enemyRows.Clear();
        actualOutcome = BattleOutcome.Unknown;
        AllySummaryText = "-";
        EnemySummaryText = "-";

        // 预测取值分两种：赛前对局没有入库记录，用收敛循环持有的那一版；
        // 历史对局读入库记录。切换选中时若直接清成 null，切回赛前对局会
        // 显示 --，这正是"切到 replay 再切回来右侧空白"的根因。
        bool isPreBattle = battle is not null
            && ReferenceEquals(currentBattle?.Battle, battle);
        currentPrediction = isPreBattle ? preBattlePrediction : null;

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
            full = EnrichWithSnapshots(full);

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
                allyRows[RowKey(p)] = row;
                Allies.Add(row);
            }

            foreach (var p in full.Enemies
                .OrderBy(p => p.IsBot)
                .ThenBy(p => PlayerRowViewModel.ShipClassSortKey(p.ShipClass))
                .ThenByDescending(p => p.ShipTier)
                .ThenBy(p => p.AccountId, StringComparer.Ordinal))
            {
                var row = PlayerRowViewModel.FromPlayer(p);
                enemyRows[RowKey(p)] = row;
                Enemies.Add(row);
            }

            // 阶段一先给一版汇总，基于快照合并结果；阶段三补拉完成后重算。
            AllySummaryText = BuildTeamSummary(full.Allies);
            EnemySummaryText = BuildTeamSummary(full.Enemies);

            // 预测取值分两种：赛前对局没有入库记录，用收敛循环持有的那一版；
            // 历史对局读入库记录。
            if (!isPreBattle)
            {
                currentPrediction = await services.Repository
                    .GetPredictionAsync(full.BattleId)
                    .ConfigureAwait(true);
            }
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
        // 补拉结果落地后增量刷新。赛前对局由收敛循环统一负责拉取与刷新，
        // 这里不重复拉，避免两处并发请求同一批玩家。
        StartBackgroundLoading(full, version, isPreBattle, token);
    }

    /// <summary>
    /// 阶段二三在后台推进时的执行顺序。
    /// </summary>
    /// <remarks>
    /// 只把阶段一留在界面同步上下文里。阶段二含网络请求与轮询延迟，若与
    /// <see cref="LoadBattleDetailsAsync"/> 共用同步上下文，在无界面环境的
    /// 单线程上下文中会把后续代码一直排队，界面线程也拿不到执行机会。
    /// </remarks>
    private void StartBackgroundLoading(BattleRecord full, int version, bool isPreBattle, CancellationToken token)
    {
        _ = Task.Run(
            () => isPreBattle
                ? FetchClanGradesAsync(full, version, token)
                : FetchMissingStatsInBackgroundAsync(full, version, token),
            CancellationToken.None);
    }

    /// <summary>
    /// 从名单中挑出需要在线补拉统计的玩家。
    /// </summary>
    /// <remarks>
    /// 账号 ID 为 -1 哨兵的也要收进来：赛前名单的 vehicles.id 是会话临时
    /// ID，解析期一律置哨兵，真账号由提供商按昵称解析。若按 -1 过滤，赛前
    /// 名单会被整批滤掉，从不去查 API，界面恒显示"隐藏"。提供商内部对
    /// Auto 服跳过、对哨兵走昵称解析，交给它处理即可。
    ///
    /// 判据与收敛口径一致：账号胜率、分舰种、单船三项任一缺失都算缺，
    /// 只拉账号胜率会让舰种与单船列永远留空。隐藏战绩者排除在外，WG API
    /// 不返回他们的数据，反复拉只会空转。
    /// internal 以便测试直接验证筛选口径。
    /// </remarks>
    internal static IEnumerable<PlayerRecord> SelectPlayersNeedingStats(
        IEnumerable<PlayerRecord> players) =>
        players
            .Where(p => p.IsStatsCandidate)
            .Where(p => !p.IsFullyCovered);

    /// <summary>
    /// 用仓库里的最新快照补齐对局特征。
    /// </summary>
    /// <remarks>
    /// 账号层取最新账号快照，带船 ID 时再取该船最新一条，两层独立命中，
    /// 单船快照缺失时退回账号层。四个调用点（详情展示、重算、补拉后收敛、
    /// 赛前迭代）用的是同一口径，改动只需改这里。
    ///
    /// 这里同步阻塞是为了让 <see cref="SnapshotFeatureBuilder.Enrich"/> 保持
    /// 纯同步签名。仓库内部全部 ConfigureAwait(false)，不会死锁；代价是每次
    /// 详情加载在界面线程上付出 2×N 次 SQLite 往返。
    /// </remarks>
    private BattleRecord EnrichWithSnapshots(BattleRecord battle) =>
        SnapshotFeatureBuilder.Enrich(battle, (accountId, server, shipId) =>
        {
            PlayerSnapshot? account = services.Repository
                .GetLatestSnapshotAsync(accountId, server)
                .GetAwaiter()
                .GetResult();

            // Enrich 只在玩家有船 ID 时传入船 ID，传入空串走的就是上面的
            // 账号级查询。这里保留空判只是防御，确保船快照缺失时退回账号层。
            if (string.IsNullOrEmpty(shipId))
            {
                return account;
            }

            return services.Repository
                .GetLatestShipSnapshotAsync(accountId, server, shipId)
                .GetAwaiter()
                .GetResult() ?? account;
        });

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
            // 赛前对局不在库里，直接用内存里的名单重算，不查库。
            bool isPreBattle = ReferenceEquals(currentBattle?.Battle, selectedBattle);
            BattleRecord? battle;
            if (isPreBattle)
            {
                battle = selectedBattle;
            }
            else
            {
                battle = await services.Repository.GetBattleAsync(selectedBattle.BattleId).ConfigureAwait(true);
            }

            if (battle is null)
            {
                return;
            }

            // 重算前先把快照合并进来：补拉过的数据不落 battle_players 表，
            // 直接预测会丢掉账号层特征。
            battle = EnrichWithSnapshots(battle);

            var prediction = await services.Predictor.PredictAsync(battle).ConfigureAwait(true);

            // 赛前对局不落库，只更新内存；历史对局落库以便校准。
            if (!isPreBattle)
            {
                await services.Repository.SavePredictionAsync(prediction).ConfigureAwait(true);
            }

            currentPrediction = prediction;
            if (isPreBattle)
            {
                preBattlePrediction = prediction;
            }

            // 同步刷新左侧列表项的预测概率，否则列表要重启才更新。
            var summary = RecentBattles.FirstOrDefault(s => s.Battle.BattleId == battle.BattleId);
            summary?.UpdatePrediction(prediction.AllyWinProbability);
            currentBattle?.UpdatePrediction(prediction.AllyWinProbability);

            StatusMessage = $"已重算 · {prediction.ModelVersion}";
            LogSink.Info($"预测重算完成: {battle.BattleId[..Math.Min(12, battle.BattleId.Length)]} 概率 {prediction.AllyWinProbability:F3}");
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
    ///
    /// 拉取与落库都走 <see cref="PlayerStatsService"/>：它按小块推进，每块
    /// 拉完立即落库。本方法常被取消（tempArenaInfo 重写会触发新一局详情，
    /// 取消上一局），整批一次性落库会把已拿到的数据全丢掉，分块落库保证
    /// 被取消时已完成的块都已持久化。
    /// </remarks>
    private async Task FetchMissingStatsInBackgroundAsync(BattleRecord battle, int version, CancellationToken token)
    {
        // 缺统计的玩家。补拉与军团分档共用这一份名单。
        List<PlayerRecord> missing = SelectPlayersNeedingStats(battle.Players).ToList();

        // 军团分档与统计补拉并行推进，缺统计名单为空时军团查询照常执行。
        var clanTask = FetchClanGradesAsync(battle, version, token);
        // 快照已被别的链路补齐时也要把进度提示收掉，否则“正在补充”会一直挂着。
        if (missing.Count == 0)
        {
            await clanTask.ConfigureAwait(false);
            Post(() =>
            {
                if (IsDetailsCurrent(version) && statusMessage.StartsWith(StatsFetchStatusPrefix, StringComparison.Ordinal))
                {
                    StatusMessage = "就绪";
                }
            });
            return;
        }

        string progressText = $"{StatsFetchStatusPrefix}{missing.Count} 名玩家的统计...";
        StatusMessage = progressText;
        LogSink.Info($"快照在线补拉开始，缺统计玩家 {missing.Count} 名");

        try
        {
            var fetchTask = services.Stats.FetchAndCacheAsync(missing, token);

            // 补拉按块推进，轮询到点把已落地的数据刷上界面。
            // 行对象按引用持有，RefreshFrom 只更新内容，不改集合结构。
            while (!fetchTask.IsCompleted)
            {
                await Task.Delay(StatsProgressInterval, token).ConfigureAwait(false);
                RefreshRowsOnUiThread(version, battle.Players);
            }

            var outcome = await fetchTask.ConfigureAwait(false);
            LogSink.Info($"快照在线补拉完成，处理 {outcome.Processed} 条，失败批次 {outcome.FailedBatches}/{outcome.TotalBatches}");

            if (outcome.Processed > 0)
            {
                // 落库顺带按保留天数清一次旧快照，失败静默。
                int deleted = await services.Repository
                    .CleanupSnapshotsAsync(services.Config.SnapshotRetentionDays)
                    .ConfigureAwait(false);
                if (deleted > 0)
                {
                    LogSink.Info($"快照清理完成，删除 {deleted} 条过期记录");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 已被新选择作废，静默退出。已落库的块不受影响。
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

        // 阶段三：快照合并后重算。分舰种聚合与单船口径都以合并结果为准，
        // 与阶段一的展示口径完全一致。合并逐玩家查库，放到线程池执行，
        // 不占界面线程。
        BattleRecord enriched = await Task
            .Run(() => EnrichWithSnapshots(battle))
            .ConfigureAwait(false);

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
                            ? allyRows.GetValueOrDefault(RowKey(player))
                            : enemyRows.GetValueOrDefault(RowKey(player));
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
                ? allyRows.GetValueOrDefault(RowKey(player))
                : enemyRows.GetValueOrDefault(RowKey(player));
            row?.RefreshFrom(player);
        }
    }

    /// <summary>
    /// 名单行的索引键。昵称在赛前解析、补拉与快照合并前后都稳定，
    /// 账号 ID 则在补拉前是 -1 哨兵，不能作键。
    /// </summary>
    private static string RowKey(PlayerRecord player) => player.PlayerName;

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

    /// <summary>
    /// 赛前链路入口：tempArenaInfo.json 出现时立即展示名单并出预测。
    /// </summary>
    /// <remarks>
    /// 观测线程回调，全部界面操作经 Post 投回界面线程。同一局按
    /// BattleId 判重：游戏写文件期间 watcher 可能多次读到，只有新对局
    /// 才走完整链路。正式回放入库后走既有 OnBattleIngested，赛前态被覆盖。
    /// </remarks>
    private void OnPreBattleDiscovered(BattleRecord battle)
    {
        Post(() => _ = HandlePreBattleAsync(battle));
    }

    /// <summary>
    /// 当前对局结束：tempArenaInfo 被游戏删除。收起实时区并把刚打完的一局
    /// 从库里补进历史（回放入库是异步的，这里重载一次列表）。
    /// </summary>
    private void OnPreBattleEnded()
    {
        Post(() =>
        {
            if (currentBattle is null)
            {
                return;
            }

            ClearCurrentBattle();
            LogSink.Info("当前对局结束，实时区收起");
            _ = RefreshAfterIngestionAsync();
        });
    }

    private async Task HandlePreBattleAsync(BattleRecord battle)
    {
        // 同局判重：键 = BattleId + 人数。加载过程中 tempArenaInfo 会随
        // 玩家陆续加入多次重写，BattleId 不变但人数递增，人数变化要放行
        // 刷新名单；人数相同的重复读取（watcher 轮询）被挡掉。
        string key = $"{battle.BattleId}:{battle.Players.Count}";
        if (preBattleKey == key)
        {
            return;
        }
        preBattleKey = key;

        // 服务器兜底链：观测器已优先从 clientrunner.log 探测（ApeRadar 口径）。
        // 探测失败（游戏未定位到、日志缺失）时记录仍是 Auto，而 WG API 补拉
        // 对 Auto 组直接跳过，整名单会全显示"隐藏"。再从库里最近一局推断补上。
        if (battle.Server == GameServer.Auto)
        {
            var inferred = await services.Repository
                .GetRecentBattlesAsync(limit: 1)
                .ConfigureAwait(true);
            if (inferred.Count > 0 && inferred[0].Server != GameServer.Auto)
            {
                battle.Server = inferred[0].Server;
                // PlayerRecord 各自带 Server 字段，补拉按它分组。BattleRecord
                // 改了不等于玩家记录改了，必须逐个同步。
                foreach (var p in battle.Players)
                {
                    p.Server = battle.Server;
                }
            }
        }

        LogSink.Info($"赛前名单捕获: {battle.Players.Count} 名玩家 · {battle.BattleType} · {battle.Server}");

        // 放进实时区。单独字段持有，正式回放入库触发的列表重建抹不掉它。
        var summary = BattleSummaryViewModel.FromPreBattle(battle);
        currentBattle = summary;
        OnPropertyChanged(nameof(LiveBattles));
        OnPropertyChanged(nameof(HasLiveBattles));

        // 上一局的收敛循环停掉，新一局重新起。循环独立于详情区选中，
        // 这样用户切到历史回放再切回来，预测与拉取都不受影响。
        // 上一局的预测与失败标志一并清掉，避免新一局短暂显示旧值。
        preBattleCts?.Cancel();
        preBattleCts?.Dispose();
        preBattleCts = new CancellationTokenSource();
        preBattlePrediction = null;
        preBattleFailed = false;
        SetCountdownText(string.Empty);

        // 直接把赛前记录灌进详情区：复用既有名单展示、快照合并与
        // 渐进补拉链路。Outcome 保持 Unknown，对照徽章自然显示"待对照"。
        // 走实时区选中属性：两个列表互斥，当前对局属于实时区。
        SelectedHistoryBattle = null;
        SelectedLiveBattle = summary;

        // 赛前收敛循环在后台独立跑，不阻塞本方法。它每 3 秒重算一次预测，
        // 期间后台补拉持续推进，直到数据齐全或连续无进展。
        _ = RunPreBattleConvergenceAsync(battle, summary, preBattleCts.Token);
    }

    /// <summary>
    /// 赛前收敛循环：每 3 秒重算预测，直到数据齐全或连续无进展。
    /// </summary>
    /// <remarks>
    /// 循环独立于详情区选中与代次。切换选中会重建详情区，但赛前对局没有
    /// 入库记录，重建后 currentPrediction 会被清成 null，表现为"切回来
    /// 右侧显示 --"。因此循环自己持有预测，并在每轮把结果写回界面。
    ///
    /// 收敛判据是全部真人（隐藏战绩除外）的账号、分舰种与单船数据都到齐，
    /// 只到账号胜率就停会让舰种与单船列留空。连续若干轮无任何新增覆盖即
    /// 判定失败，停止并红色提醒，避免死循环。
    /// </remarks>
    private async Task RunPreBattleConvergenceAsync(
        BattleRecord battle,
        BattleSummaryViewModel summary,
        CancellationToken token)
    {
        int consecutiveMisses = 0;
        try
        {
            int round = 0;
            while (!token.IsCancellationRequested)
            {
                round++;

                // 本轮补拉：只拉还没到齐的玩家，拉到即落库缓存。
                var missing = SelectPlayersNeedingStats(battle.Players).ToList();
                int before = CountCoveredPlayers(battle);

                if (missing.Count > 0)
                {
                    try
                    {
                        await services.Stats.FetchAndCacheAsync(missing, token).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                // 重新合并快照后推理：补拉的数据只落快照表，不合并拿不到。
                var enriched = EnrichWithSnapshots(battle);

                // 合并后的军团标签写回原始名单记录：赛前 tempArenaInfo 不含标签，
                // 原始记录本为空，写回后供军团分档查询与列表项使用。只补空值。
                foreach (var enrichedPlayer in enriched.Players)
                {
                    if (string.IsNullOrEmpty(enrichedPlayer.ClanTag))
                    {
                        continue;
                    }

                    var original = battle.Players.FirstOrDefault(
                        p => string.Equals(p.PlayerName, enrichedPlayer.PlayerName, StringComparison.Ordinal));
                    if (original is not null && string.IsNullOrEmpty(original.ClanTag))
                    {
                        original.ClanTag = enrichedPlayer.ClanTag;
                    }
                }

                // 军团标签就位后查分档，让标签按强度着色。
                await FetchClanGradesAsync(battle, detailsVersion, token).ConfigureAwait(true);

                var prediction = await services.Predictor.PredictAsync(enriched).ConfigureAwait(true);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                // 循环自己持有预测，切选中重建详情区不会把它抹掉。
                preBattlePrediction = prediction;
                currentPrediction = prediction;

                int after = CountCoveredPlayers(battle);
                (int total, int humans, int covered, int hidden, int incomplete) = DescribeCoverage(battle);

                // 分母用名单总人数：x/y 要和界面里看得到的人数对得上。
                string donePart = $"{covered + hidden}/{total}";

                // 隐藏只在确认后才出现在文案里，缺数据的玩家不再被描述成隐藏。
                string hiddenPart = hidden > 0 ? $"，另有 {hidden} 名隐藏战绩" : string.Empty;

                preBattleProgressText = incomplete == 0
                    ? $"数据齐全 {donePart}{hiddenPart}"
                    : $"已拉取 {donePart}{hiddenPart}";

                LogSink.Info($"赛前预测第 {round} 轮: 概率 {prediction.AllyWinProbability:F3}，"
                    + $"名单 {total}，真人 {humans}，覆盖 {covered}，隐藏 {hidden}，不完整 {incomplete}");

                Post(() =>
                {
                    summary.UpdatePrediction(prediction.AllyWinProbability);

                    // 只有当详情区正显示这一局赛前对局时才回写，用户切到
                    // 历史回放时不得用赛前数据污染那份名单。
                    if (ReferenceEquals(selectedBattle, battle))
                    {
                        currentPrediction = prediction;
                        // 用合并快照后的记录刷行：赛前 tempArenaInfo 不含军团标签，
                        // 合并后的记录从快照补上了标签，行才能显示军团。
                        RefreshRowsFor(detailsVersion, enriched.Players);
                        AllySummaryText = BuildTeamSummary(enriched.Allies);
                        EnemySummaryText = BuildTeamSummary(enriched.Enemies);
                        NotifyDetailsChanged();
                    }
                });

                if (incomplete == 0)
                {
                    preBattleFailed = false;
                    SetCountdownText($"预测就绪 · {preBattleProgressText}");
                    return;
                }

                // 连续无进展：本轮覆盖数没有增长。达到上限即停，红色提醒。
                consecutiveMisses = after > before ? 0 : consecutiveMisses + 1;
                if (consecutiveMisses >= PreBattleMaxConsecutiveMisses)
                {
                    preBattleFailed = true;
                    SetCountdownText($"补拉受阻 · 已停止 · {preBattleProgressText}");
                    LogSink.Warn($"赛前补拉连续 {consecutiveMisses} 轮无进展，停止收敛");
                    return;
                }

                // 3 秒倒计时给用户"正在更新"的感知，期间后台补拉持续推进。
                for (int remain = 3; remain > 0 && !token.IsCancellationRequested; remain--)
                {
                    SetCountdownText($"{remain}s 后更新预测 · 第 {round} 轮 · {preBattleProgressText}");
                    await Task.Delay(1000, token).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 换局或对局结束，静默退出。
        }
        catch (Exception ex)
        {
            preBattleFailed = true;
            SetCountdownText($"预测失败: {ex.Message}");
            LogSink.Warn($"赛前预测失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 统计覆盖情况：总人数、真人数、已有统计的真人、确认隐藏的真人、仍缺数据的真人。
    /// </summary>
    /// <remarks>
    /// 不完整指账号胜率、分舰种或单船数据任一缺失。隐藏战绩玩家 WG API
    /// 永远不返回数据，不计入不完整，否则循环永不收敛。
    ///
    /// 三项分开数，互不重叠：隐藏只看提供商是否确认，不完整只看数据是否
    /// 到齐，没拉到的玩家落在不完整里。若把“没查到”也算隐藏，显示层会
    /// 冒出根本没被标记过的隐藏玩家，覆盖数也对不上。
    ///
    /// Total 与 Humans 分开是给显示用的：名单里夹着机器人，x/y 的分母
    /// 要和名单总人数一致，隐藏与缺数据只从真人里数。
    /// </remarks>
    internal static (int Total, int Humans, int Covered, int Hidden, int Incomplete) DescribeCoverage(
        BattleRecord battle)
    {
        var humans = battle.HumanPlayers.ToList();
        int hidden = humans.Count(p => p.IsHidden);
        // covered 用与 incomplete 同一套齐全口径：账号、分舰种、单船都到齐。
        // 若只数账号胜率，"已拉取 x/y"会在舰种与单船仍空时显示成满。
        int covered = humans.Count(p => !p.IsHidden && p.IsFullyCovered);
        int incomplete = humans.Count(p => !p.IsHidden && !p.IsFullyCovered);
        return (battle.Players.Count, humans.Count, covered, hidden, incomplete);
    }

    /// <summary>
    /// 把一行刷新投到界面线程。后台线程轮询时用，不能直接改行对象。
    /// </summary>
    private void RefreshRowsOnUiThread(int version, IEnumerable<PlayerRecord> players)
    {
        // 必须先把记录复制成独立序列，再进队列。调用方传的是实时观察集合，
        // 排队后再枚举会和界面线程的增删撞车，抛“并发修改了非并发集合”。
        var snapshot = players.ToList();
        Post(() => RefreshRowsFor(version, snapshot));
    }

    /// <summary>已覆盖的真人数量，用于判定本轮是否有进展。</summary>
    private static int CountCoveredPlayers(BattleRecord battle) =>
        battle.HumanPlayers.Count(p => !p.IsHidden && p.IsFullyCovered);

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

            // 配置换绑会重建观测器，旧的当前对局不再有事件驱动，收起。
            ClearCurrentBattle();

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
