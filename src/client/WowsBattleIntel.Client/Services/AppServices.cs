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
    private bool disposed;

    private AppServices(
        AppConfig config,
        SqliteBattleRepository repository,
        IPlayerStatsProvider statsProvider,
        ClanCatalogService clanCatalog,
        IPredictor predictor,
        PredictionFallbackInfo predictionInfo,
        ReplayObserver observer)
    {
        Config = config;
        Repository = repository;
        StatsProvider = statsProvider;
        ClanCatalog = clanCatalog;
        Predictor = predictor;
        PredictionInfo = predictionInfo;
        Logs = new LogService();
        ReplayParser = new ReplayService(config);
        Ingestion = new BattleIngestionService(ReplayParser, repository, predictor);
        Observer = observer;
        // 委托在调用时读取 Ingestion 属性，重配置后自动指向新实例。
        Monitor = new LiveBattleMonitor(
            (path, token) => Ingestion.IngestAsync(path, token),
            observer,
            logs: Logs);
    }

    /// <summary>当前配置。配置变更后会被替换。</summary>
    public AppConfig Config { get; private set; }

    /// <summary>本地库仓储。</summary>
    public SqliteBattleRepository Repository { get; private set; }

    /// <summary>玩家统计来源。</summary>
    public IPlayerStatsProvider StatsProvider { get; private set; }

    /// <summary>军团强度目录，按标签提供分档查询。</summary>
    public ClanCatalogService ClanCatalog { get; private set; }

    /// <summary>预测器。ONNX 可用时为模型，否则为基线。</summary>
    public IPredictor Predictor { get; private set; }

    /// <summary>预测器装配信息，含是否降级及原因。</summary>
    public PredictionFallbackInfo PredictionInfo { get; private set; }

    /// <summary>运行日志服务。进程级共享，重配置不重建，日志跨配置版本保留。</summary>
    public LogService Logs { get; }

    /// <summary>回放解析服务。</summary>
    public ReplayService ReplayParser { get; private set; }

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
        var statsProvider = CreateStatsProvider(config);
        var clanCatalog = new ClanCatalogService(CreateWgOptions(config), repository);

        // 预测器。模型不可用时自动降级到基线。
        var createResult = PredictorFactory.Create(config.ModelPath, config.ModelVersion);
        var info = new PredictionFallbackInfo(
            createResult.UsedFallback,
            createResult.FallbackReason);

        var observer = new ReplayObserver(config);
        var services = new AppServices(
            config, repository, statsProvider, clanCatalog, createResult.Predictor, info, observer);

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
    }

    /// <summary>
    /// 停止回放观测，保留服务以便再次启动。
    /// </summary>
    public void StopObservation()
    {
        Monitor.Stop();
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
        var observer = new ReplayObserver(updated);

        lock (reconfigureLock)
        {
            Config = updated;
            Repository = repository;
            StatsProvider = CreateStatsProvider(updated);
            ClanCatalog = new ClanCatalogService(CreateWgOptions(updated), repository);
            Predictor = createResult.Predictor;
            PredictionInfo = new PredictionFallbackInfo(
                createResult.UsedFallback,
                createResult.FallbackReason);
            ReplayParser = new ReplayService(updated);
            Ingestion = new BattleIngestionService(ReplayParser, repository, Predictor);
            Observer = observer;
        }

        // 换绑在锁外做，它会启停后台协程，不应长时间持锁。
        Monitor.Rebind(observer);
    }

    private static IPlayerStatsProvider CreateStatsProvider(AppConfig config)
    {
        var options = CreateWgOptions(config);

        // 全量船只查询一次覆盖单船统计、分舰种聚合与常用等级，与 CLI 补采同口径。
        // 关闭时逐玩家单船查询拿不到熟练数据，界面舰种列永远空。
        options.FetchClassStats = true;

        return new WgPublicStatsProvider(options);
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
        (Predictor as IDisposable)?.Dispose();
        Logs.Info("应用退出");
    }
}

/// <summary>
/// 预测器装配信息。
/// </summary>
/// <param name="UsedFallback">是否降级到了基线预测器。</param>
/// <param name="Reason">降级原因，未降级时为空。</param>
public sealed record PredictionFallbackInfo(bool UsedFallback, string? Reason);
