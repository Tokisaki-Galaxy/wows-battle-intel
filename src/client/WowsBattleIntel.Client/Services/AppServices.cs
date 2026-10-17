using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Prediction;
using WowsBattleIntel.Stats;
using WowsBattleIntel.Storage;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 应用级服务装配。
/// </summary>
/// <remarks>
/// 集中创建并持有各层依赖，供视图取用。用显式装配而非依赖注入容器，
/// 项目规模不需要容器，显式装配更容易看懂依赖关系。
///
/// 实现 IDisposable 以释放 ONNX 会话与数据库连接等资源。
/// </remarks>
public sealed class AppServices : IDisposable
{
    private readonly object reconfigureLock = new();

    // 当前日志落盘选项的副本。Config 会在重配置时被替换，另存一份才能与配置变更前的口径比较。
    private LogFileOptions appliedLogOptions;

    private bool disposed;

    private AppServices(
        AppConfig config,
        SqliteBattleRepository repository,
        IPlayerStatsProvider statsProvider,
        ClanCatalogService clanCatalog,
        IPredictor predictor,
        PredictionFallbackInfo predictionInfo,
        LogService logs,
        ReplayObserver observer)
    {
        Config = config;
        appliedLogOptions = config.LogOptions;
        Repository = repository;
        StatsProvider = statsProvider;
        Logs = logs;
        Stats = new PlayerStatsService(statsProvider, Logs);
        ClanCatalog = clanCatalog;
        // 名单染色规则是全局共享的，装配时同步一次，配置变更时再更新。
        ColorRuleSet.Set(config.ColorRules);
        Predictor = predictor;
        PredictionInfo = predictionInfo;
        ReplayParser = new ReplayService(config);
        Ingestion = new BattleIngestionService(ReplayParser, repository, predictor);
        // 补录持有配置读取委托与入库委托，配置换绑后自动指向新实例。
        Backfill = new ReplayBackfillService(
            () => Config,
            repository,
            Ingestion,
            () => repository.GetStartedAtValuesAsync(),
            Logs);
        Observer = observer;
        // 委托在调用时读取 Ingestion 属性，重配置后自动指向新实例。
        Monitor = new LiveBattleMonitor(
            (path, token) => Ingestion.IngestAsync(path, token),
            observer,
            logs: Logs);
        ArenaWatcher = new ArenaInfoWatcher(config);
    }

    /// <summary>赛前名单观测器。常驻实例，配置变更走 Rebind 换绑。</summary>
    public ArenaInfoWatcher ArenaWatcher { get; }

    /// <summary>当前配置。配置变更后会被替换。</summary>
    public AppConfig Config { get; private set; }

    /// <summary>本地库仓储。</summary>
    public SqliteBattleRepository Repository { get; private set; }

    /// <summary>玩家统计来源。</summary>
    public IPlayerStatsProvider StatsProvider { get; private set; }

    /// <summary>
    /// 玩家战绩的统一入口：拉取并落库缓存。所有战绩查询都走这里。
    /// </summary>
    public PlayerStatsService Stats { get; private set; }

    /// <summary>军团强度目录，按标签提供分档查询。</summary>
    public ClanCatalogService ClanCatalog { get; private set; }

    /// <summary>预测器。ONNX 可用时为模型，否则为基线。</summary>
    public IPredictor Predictor { get; private set; }

    /// <summary>预测器装配信息，含是否降级及原因。</summary>
    public PredictionFallbackInfo PredictionInfo { get; private set; }

    /// <summary>运行日志服务。进程级共享，重配置时换绑落盘写入器，日志跨配置版本保留。</summary>
    public LogService Logs { get; private set; }

    /// <summary>回放解析服务。</summary>
    public ReplayService ReplayParser { get; private set; }

    /// <summary>
    /// 历史回放补录服务：找出回放目录里尚未入库的对局，供列表以占位项展示，
    /// 点开时才解析入库。
    /// </summary>
    public ReplayBackfillService Backfill { get; private set; }

    /// <summary>战斗入库服务。</summary>
    public BattleIngestionService Ingestion { get; private set; }

    /// <summary>回放目录观测器。</summary>
    public ReplayObserver Observer { get; private set; }

    /// <summary>
    /// 实时战斗监控，串联观测与解析。
    /// </summary>
    /// <remarks>
    /// 配置变更时换绑观测器而非重建实例，订阅者才能跨重配置保持有效。
    /// </remarks>
    public LiveBattleMonitor Monitor { get; }

    /// <summary>
    /// 创建并初始化全部服务。
    /// </summary>
    public static async Task<AppServices> CreateAsync(
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 本地库。
        var repository = new SqliteBattleRepository(config.DatabasePath);
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // 统计来源。军团目录带持久缓存，仓储实现 Core 的缓存接口。
        var statsProvider = CreateStatsProvider(config, repository);
        var clanCatalog = new ClanCatalogService(CreateWgOptions(config), repository);

        // 预测器。模型不可用时自动降级到基线。
        var createResult = PredictorFactory.Create(config.ModelPath, config.ModelVersion);
        var info = new PredictionFallbackInfo(
            createResult.UsedFallback,
            createResult.FallbackReason);

        // 启动场景：首次扫描补上应用没运行期间打完的场次。委托在调用时
        // 读实例，观测器在构造之后才启动，那时 servicesRef 已赋值。
        AppServices? servicesRef = null;
        var observer = new ReplayObserver(
            config,
            (path, startedAt) => servicesRef is { } ready
                ? ready.IsReplayKnownAsync(path, startedAt)
                : Task.FromResult(true));
        var logs = new LogService(CreateLogWriter(config));
        var services = new AppServices(
            config, repository, statsProvider, clanCatalog, createResult.Predictor, info, logs, observer);
        servicesRef = services;

        // 启动时按保留口径清一次旧日志。写入器内部已吞异常，不会阻断启动。
        logs.TrimLogFiles(config.LogOptions);

        // 启动时按保留天数清一次旧快照。fire and forget，失败记日志即可，
        // 不阻塞启动。
        _ = CleanupSnapshotsSilentlyAsync(services);

        return services;
    }

    /// <summary>
    /// 后台执行快照清理，异常静默记日志。
    /// </summary>
    private static async Task CleanupSnapshotsSilentlyAsync(AppServices services)
    {
        try
        {
            int deleted = await services.Repository
                .CleanupSnapshotsAsync(services.Config.SnapshotRetentionDays)
                .ConfigureAwait(false);
            if (deleted > 0)
            {
                services.Logs.Info($"快照清理完成，删除 {deleted} 条过期记录");
            }
        }
        catch (Exception ex)
        {
            services.Logs.Warn($"快照清理失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 按配置启动回放观测。
    /// </summary>
    public void StartObservation()
    {
        if (disposed)
        {
            return;
        }

        Logs.Info("观测启动");
        Monitor.Start();
        ArenaWatcher.Start();
    }

    /// <summary>
    /// 判断一份回放是否已入库。供观测器首次扫描逐份比对，
    /// 库里没有的回放才进入解析队列。
    /// </summary>
    /// <param name="replayPath">回放路径，日志与诊断用。</param>
    /// <param name="startedAt">文件名里的开战时间。</param>
    public Task<bool> IsReplayKnownAsync(string replayPath, DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayPath);
        return Backfill.IsKnownAsync(startedAt);
    }

    /// <summary>
    /// 停止回放观测，保留服务以便再次启动。
    /// </summary>
    public void StopObservation()
    {
        Monitor.Stop();
        ArenaWatcher.Stop();
        Logs.Info("观测停止");
    }

    /// <summary>
    /// 应用新配置并重建受影响的组件。
    /// </summary>
    /// <remarks>
    /// 配置里的路径在构造时就被各组件捕获，改配置必须重建组件才生效。
    /// 数据库路径变化会新建库文件，但旧库数据保留在原路径，不会迁移。
    /// </remarks>
    /// <param name="updated">新配置，已写盘。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task ReconfigureAsync(
        AppConfig updated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        ObjectDisposedException.ThrowIf(disposed, this);

        // 重建期间先停掉观测与推理，避免旧组件继续投递路径或占用模型文件。
        Monitor.Stop();
        (Predictor as IDisposable)?.Dispose();

        var repository = new SqliteBattleRepository(updated.DatabasePath);
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var createResult = PredictorFactory.Create(updated.ModelPath, updated.ModelVersion);
        // 换绑场景：首次扫描只登记不通知，避免设置页一保存就把目录里的
        // 全部历史回放重新塞进解析队列。漏掉的场次由列表占位项按需补。
        var observer = new ReplayObserver(
            updated,
            (path, startedAt) => IsReplayKnownAsync(path, startedAt),
            reportOnFirstScan: false);

        lock (reconfigureLock)
        {
            Config = updated;
            Repository = repository;
            StatsProvider = CreateStatsProvider(updated, repository);
            Stats = new PlayerStatsService(StatsProvider, Logs);
            ClanCatalog = new ClanCatalogService(CreateWgOptions(updated), repository);
            ColorRuleSet.Set(updated.ColorRules);
            Predictor = createResult.Predictor;
            PredictionInfo = new PredictionFallbackInfo(
                createResult.UsedFallback,
                createResult.FallbackReason);
            ReplayParser = new ReplayService(updated);
            Ingestion = new BattleIngestionService(ReplayParser, repository, Predictor);
            Observer = observer;
            Backfill = new ReplayBackfillService(
                () => Config,
                repository,
                Ingestion,
                () => repository.GetStartedAtValuesAsync(),
                Logs);
        }

        // 换绑在锁外做，它会启停后台协程，不应长时间持锁。
        Monitor.Rebind(observer);

        // 赛前观测器常驻，只换配置，事件订阅保留。
        ArenaWatcher.Rebind(updated);

        // 日志目录或保留口径变了才换写入器。LogService 实例只换绑定，不重建，
        // 界面侧订阅的事件因此始终有效，日志页签不会在保存设置后冻结。
        var newOptions = updated.LogOptions;
        if (!SameLogOptions(appliedLogOptions, newOptions))
        {
            Logs.RebindWriter(CreateLogWriter(updated));
            appliedLogOptions = newOptions;
            Logs.Info("日志落盘设置已更新");
        }
    }

    /// <summary>
    /// 创建日志写入器。目录不可用时返回 null，退化为只写内存。
    /// </summary>
    private static ILogFileWriter? CreateLogWriter(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            return new LogFileWriter(config.LogOptions);
        }
        catch (Exception)
        {
            // 落盘初始化失败不该阻断启动，内存日志照常可用。
            return null;
        }
    }

    /// <summary>比较两份落盘选项是否等价。</summary>
    private static bool SameLogOptions(LogFileOptions a, LogFileOptions b) =>
        string.Equals(a.Directory, b.Directory, StringComparison.OrdinalIgnoreCase)
        && a.RetentionDays == b.RetentionDays
        && a.MaxBytes == b.MaxBytes
        && a.Mode == b.Mode;

    private static IPlayerStatsProvider CreateStatsProvider(AppConfig config, IPlayerStatsCache? cache)
    {
        var options = CreateWgOptions(config);

        // 全量船只查询一次覆盖单船统计、分舰种聚合与常用等级，与 CLI 补采同口径。
        // 关闭时逐玩家单船查询拿不到熟练数据，界面舰种列永远空。
        options.FetchClassStats = true;

        var raw = new WgPublicStatsProvider(options);

        // 统一加一层缓存装饰器：查缓存、TTL 判定、回写都在装饰器里，
        // 任何具体提供商套上它都自动获得加速能力。开关只控制是否读缓存，
        // 快照始终落库。
        return new CachingPlayerStatsProvider(raw, cache, new PlayerStatsCacheOptions
        {
            Enabled = config.EnableBattleCache,
            Ttl = config.BattleCacheTtl,
        });
    }

    private static WgPublicOptions CreateWgOptions(AppConfig config)
    {
        var options = new WgPublicOptions();
        if (!string.IsNullOrWhiteSpace(config.WgApplicationId))
        {
            options.ApplicationId = config.WgApplicationId;
        }

        return options;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Monitor.Dispose();
        ArenaWatcher.Dispose();
        (Predictor as IDisposable)?.Dispose();
        Logs.Info("应用退出");
        Logs.Dispose();
    }
}

/// <summary>
/// 预测器装配信息。
/// </summary>
/// <param name="UsedFallback">是否降级到了基线预测器。</param>
/// <param name="Reason">降级原因，未降级时为空。</param>
public sealed record PredictionFallbackInfo(bool UsedFallback, string? Reason);
