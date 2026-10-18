using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Prediction;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Prediction;

/// <summary>
/// 用 ONNX 模型做本地预测。
/// </summary>
/// <remarks>
/// 见 ADR 0003：推理在客户端本地进行，模型文件由服务器下发。
/// 模型不可用或推理失败时应降级到 <see cref="BaselinePredictor"/>，
/// 由调用方决定降级策略，本类只负责 ONNX 路径。
///
/// 输入特征顺序必须与训练时一致，由 FeatureVectorBuilder 保证。
/// </remarks>
public sealed class OnnxPredictor : IPredictor, IDisposable
{
    private readonly InferenceSession session;
    private readonly string inputName;
    private readonly string outputName;
    private readonly TeamAggregationOptions aggregationOptions;
    private readonly BaselinePredictor fallback = new();
    private bool disposed;

    /// <summary>
    /// 从模型文件加载。
    /// </summary>
    /// <param name="modelPath">ONNX 模型文件路径。</param>
    /// <param name="modelVersion">模型版本标识。</param>
    /// <param name="aggregationOptions">队伍聚合阈值。</param>
    public OnnxPredictor(
        string modelPath,
        string modelVersion,
        TeamAggregationOptions? aggregationOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"模型文件不存在: {modelPath}", modelPath);
        }

        session = new InferenceSession(modelPath);
        inputName = session.InputMetadata.Keys.First();

        // 按名称优先取概率输出，与 Python 端导出一致。
        outputName = session.OutputMetadata.Keys.FirstOrDefault(
            k => k.Equals("probabilities", StringComparison.OrdinalIgnoreCase))
            ?? session.OutputMetadata.Keys.Last();

        ModelVersion = modelVersion;
        ModelHash = ComputeFileHash(modelPath);
        this.aggregationOptions = aggregationOptions ?? TeamAggregationOptions.Default;
    }

    /// <inheritdoc />
    public string ModelVersion { get; }

    /// <inheritdoc />
    public string ModelHash { get; }

    /// <summary>
    /// 预测。
    /// </summary>
    public Task<PredictionResult> PredictAsync(
        BattleRecord battle,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(battle);
        cancellationToken.ThrowIfCancellationRequested();

        var vector = FeatureVectorBuilder.FromBattle(battle, aggregationOptions);

        // 纯人机局分支：敌方没有可观测的真人，特征向量是"有人对无人"的
        // 假象，模型没见过这种输入，读数不可信。与基线预测器同口径：固定
        // 0.5，置信度压半，标记特征不可用。
        if (battle.EnemyHasNoObservableHumans)
        {
            int allBotHumans = battle.HumanPlayers.Count();
            int allBotWithStats = battle.HumanPlayers.Count(p => p.HasStats);
            double allBotCoverage = allBotHumans > 0 ? (double)allBotWithStats / allBotHumans : 0;

            var allBotAlly = TeamAggregator.Aggregate(battle.Allies, aggregationOptions);
            var allBotEnemy = TeamAggregator.Aggregate(battle.Enemies, aggregationOptions);
            var allBotDiff = TeamAggregator.ComputeDiff(allBotAlly, allBotEnemy);

            return Task.FromResult(new PredictionResult
            {
                BattleId = battle.BattleId,
                PredictedAt = DateTimeOffset.UtcNow,
                AllyWinProbability = 0.5,
                Confidence = Math.Min(allBotCoverage, 0.5),
                ModelVersion = ModelVersion,
                ModelHash = ModelHash,
                FeaturesAvailable = false,
                FeatureSnapshotJson = FeatureSnapshot.Serialize(allBotAlly, allBotEnemy, allBotDiff),
            });
        }

        double probability = RunInference(vector);

        int totalHumans = battle.HumanPlayers.Count();
        int withStats = battle.HumanPlayers.Count(p => p.HasStats);
        double coverage = totalHumans > 0 ? (double)withStats / totalHumans : 0;

        var allyFeatures = TeamAggregator.Aggregate(battle.Allies, aggregationOptions);
        var enemyFeatures = TeamAggregator.Aggregate(battle.Enemies, aggregationOptions);
        var diff = TeamAggregator.ComputeDiff(allyFeatures, enemyFeatures);

        return Task.FromResult(new PredictionResult
        {
            BattleId = battle.BattleId,
            PredictedAt = DateTimeOffset.UtcNow,
            AllyWinProbability = probability,
            Confidence = coverage,
            ModelVersion = ModelVersion,
            ModelHash = ModelHash,
            FeaturesAvailable = coverage >= 0.999,
            FeatureSnapshotJson = FeatureSnapshot.Serialize(allyFeatures, enemyFeatures, diff),
        });
    }

    /// <summary>
    /// 执行 ONNX 推理。
    /// </summary>
    private double RunInference(FeatureVector vector)
    {
        var tensor = new DenseTensor<float>(vector.Values, [1, vector.Values.Length]);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor),
        };

        using var results = session.Run(inputs);
        var output = results.First(r => r.Name == outputName).AsTensor<float>();

        // 输出形状通常为 [1, 2] 的概率对，取正类；
        // 单个标签输出或已压缩为标量时直接取首元素。
        if (output.Dimensions.Length == 2 && output.Dimensions[1] == 2)
        {
            return output[0, 1];
        }

        return output.GetValue(0);
    }

    /// <summary>
    /// 计算模型文件的 SHA256。
    /// </summary>
    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!disposed)
        {
            session.Dispose();
            disposed = true;
        }
    }
}

