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
    private bool disposed;

    private AppServices(
        AppConfig config,
        SqliteBattleRepository repository,
        IPlayerStatsProvider statsProvider,
        IPredictor predictor,
        PredictionFallbackInfo predictionInfo)
    {
        Config = config;
        Repository = repository;
        StatsProvider = statsProvider;
        Predictor = predictor;
        PredictionInfo = predictionInfo;
        ReplayParser = new ReplayService(config);
        Ingestion = new BattleIngestionService(ReplayParser, repository, predictor);
        Observer = new ReplayObserver(config);
    }

    /// <summary>当前配置。</summary>
    public AppConfig Config { get; }

    /// <summary>本地库仓储。</summary>
    public SqliteBattleRepository Repository { get; }

    /// <summary>玩家统计来源。</summary>
    public IPlayerStatsProvider StatsProvider { get; }

    /// <summary>预测器。ONNX 可用时为模型，否则为基线。</summary>
    public IPredictor Predictor { get; }

    /// <summary>预测器装配信息，含是否降级及原因。</summary>
    public PredictionFallbackInfo PredictionInfo { get; }

    /// <summary>回放解析服务。</summary>
    public ReplayService ReplayParser { get; }

    /// <summary>战斗入库服务。</summary>
    public BattleIngestionService Ingestion { get; }

    /// <summary>回放目录观测器。</summary>
    public ReplayObserver Observer { get; }

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

        // 统计来源。
        var statsOptions = new WgPublicOptions();
        if (!string.IsNullOrWhiteSpace(config.WgApplicationId))
        {
            statsOptions.ApplicationId = config.WgApplicationId;
        }

        IPlayerStatsProvider statsProvider = new WgPublicStatsProvider(statsOptions);

        // 预测器。模型不可用时自动降级到基线。
        var createResult = PredictorFactory.Create(config.ModelPath, config.ModelVersion);
        var info = new PredictionFallbackInfo(
            createResult.UsedFallback,
            createResult.FallbackReason);

        return new AppServices(config, repository, statsProvider, createResult.Predictor, info);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Observer.Dispose();
        (Predictor as IDisposable)?.Dispose();
        disposed = true;
    }
}

/// <summary>
/// 预测器装配信息。
/// </summary>
/// <param name="UsedFallback">是否降级到了基线预测器。</param>
/// <param name="Reason">降级原因，未降级时为空。</param>
public sealed record PredictionFallbackInfo(bool UsedFallback, string? Reason);
