using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Core.Prediction;

/// <summary>
/// 朴素基线预测器。
/// </summary>
/// <remarks>
/// 这是整个项目的地基，不是临时凑数的东西。它的作用有三个：
///
/// 1. 在没有训练数据时就能跑通"预测到对照"的完整数据流。
/// 2. 作为基线，任何训练模型都必须显著优于它才有存在价值。
/// 3. 作为降级路径，模型不可用或特征缺失时接管。
///
/// 方法：用双方加权胜率差过一个逻辑函数，映射成概率。
/// 这是 XVM 那套思路的直译，简单、可解释、无需训练。
/// </remarks>
public sealed class BaselinePredictor : IPredictor
{
    /// <summary>
    /// 逻辑函数的斜率。数值越大，胜率差对概率的影响越陡。
    /// 默认值取自经验，使 5 个百分点的队伍胜率差对应约 10 个百分点的
    /// 预测概率偏移，与社区观测的合理量级接近。
    /// </summary>
    public const double DefaultSlope = 4.0;

    /// <summary>默认模型版本标识。</summary>
    public const string DefaultModelVersion = "baseline-logistic-v1";

    private readonly double slope;
    private readonly TeamAggregationOptions aggregationOptions;

    /// <summary>
    /// 构造基线预测器。
    /// </summary>
    /// <param name="slope">逻辑函数斜率，默认 4.0。</param>
    /// <param name="aggregationOptions">队伍聚合阈值。</param>
    public BaselinePredictor(
        double slope = DefaultSlope,
        TeamAggregationOptions? aggregationOptions = null)
    {
        if (slope <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slope), "斜率必须为正数。");
        }

        this.slope = slope;
        this.aggregationOptions = aggregationOptions ?? TeamAggregationOptions.Default;
    }

    /// <inheritdoc />
    public string ModelVersion => DefaultModelVersion;

    /// <inheritdoc />
    public string ModelHash => string.Empty;

    /// <inheritdoc />
    public Task<PredictionResult> PredictAsync(
        BattleRecord battle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(battle);
        cancellationToken.ThrowIfCancellationRequested();

        // 纯人机局分支：敌方没有可观测的真人，特征差全是"有人对无人"的
        // 假象，逻辑函数会把概率推满。给出固定保守读数：胜率 0.5，
        // 置信度按数据覆盖正常算但压到 0.5 以下，明确表达"读数不可信"。
        if (battle.EnemyHasNoObservableHumans)
        {
            var allyFeatures = TeamAggregator.Aggregate(battle.Allies, aggregationOptions);
            var enemyFeatures = TeamAggregator.Aggregate(battle.Enemies, aggregationOptions);
            var diff = TeamAggregator.ComputeDiff(allyFeatures, enemyFeatures);

            int totalHumans = battle.HumanPlayers.Count();
            int withStats = battle.HumanPlayers.Count(p => p.HasStats);
            double coverage = totalHumans > 0 ? (double)withStats / totalHumans : 0;

            return Task.FromResult(new PredictionResult
            {
                BattleId = battle.BattleId,
                PredictedAt = DateTimeOffset.UtcNow,
                AllyWinProbability = 0.5,
                Confidence = Math.Min(coverage, 0.5),
                ModelVersion = ModelVersion,
                ModelHash = ModelHash,
                FeaturesAvailable = false,
                FeatureSnapshotJson = FeatureSnapshot.Serialize(allyFeatures, enemyFeatures, diff),
            });
        }

        var allyAgg = TeamAggregator.Aggregate(battle.Allies, aggregationOptions);
        var enemyAgg = TeamAggregator.Aggregate(battle.Enemies, aggregationOptions);
        var teamDiff = TeamAggregator.ComputeDiff(allyAgg, enemyAgg);

        // 优先用加权胜率差，缺失时退回账号胜率差。
        bool weightedAvailable = allyAgg.AvgWeightedWinrate > 0
            || enemyAgg.AvgWeightedWinrate > 0;
        double primaryDiff = weightedAvailable
            ? teamDiff.WeightedWinrateDiff
            : teamDiff.WinrateDiff;

        double probability = Logistic(primaryDiff * slope);

        // 置信度：数据越完整越自信。这里用有统计数据的玩家占比衡量。
        int normalHumans = battle.HumanPlayers.Count();
        int normalWithStats = battle.HumanPlayers.Count(p => p.HasStats);
        double normalCoverage = normalHumans > 0 ? (double)normalWithStats / normalHumans : 0;

        var result = new PredictionResult
        {
            BattleId = battle.BattleId,
            PredictedAt = DateTimeOffset.UtcNow,
            AllyWinProbability = probability,
            Confidence = normalCoverage,
            ModelVersion = ModelVersion,
            ModelHash = ModelHash,
            FeaturesAvailable = normalCoverage >= 0.999,
            FeatureSnapshotJson = FeatureSnapshot.Serialize(allyAgg, enemyAgg, teamDiff),
        };

        return Task.FromResult(result);
    }

    /// <summary>
    /// 标准逻辑函数，把任意实数压到 0 到 1。
    /// </summary>
    private static double Logistic(double x) => 1.0 / (1.0 + Math.Exp(-x));
}
