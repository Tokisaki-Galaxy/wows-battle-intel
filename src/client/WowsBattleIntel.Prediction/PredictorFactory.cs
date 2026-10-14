using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Prediction;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Prediction;

/// <summary>
/// 预测器工厂。
/// </summary>
/// <remarks>
/// 封装"优先用模型，不行就降级"的策略，见 ADR 0003。
/// 模型文件缺失、加载失败、推理异常时都降级到
/// <see cref="BaselinePredictor"/>，保证预测功能不因模型问题而中断。
///
/// 降级不是静默的：调用方可通过 <see cref="CreateResult"/> 得知实际用了哪条路径。
/// </remarks>
public static class PredictorFactory
{
    /// <summary>
    /// 创建预测器的结果，含实际使用的路径。
    /// </summary>
    public sealed class CreateResult
    {
        /// <summary>实际使用的预测器。</summary>
        public required IPredictor Predictor { get; init; }

        /// <summary>是否降级到了基线预测器。</summary>
        public bool UsedFallback { get; init; }

        /// <summary>降级原因，未降级时为空。</summary>
        public string? FallbackReason { get; init; }
    }

    /// <summary>
    /// 创建预测器。优先用 ONNX 模型，失败时降级到基线。
    /// </summary>
    /// <param name="modelPath">ONNX 模型路径。为空或不存在时直接降级。</param>
    /// <param name="modelVersion">模型版本标识。</param>
    /// <param name="aggregationOptions">队伍聚合阈值。</param>
    public static CreateResult Create(
        string? modelPath,
        string modelVersion,
        TeamAggregationOptions? aggregationOptions = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return new CreateResult
            {
                Predictor = new BaselinePredictor(aggregationOptions: aggregationOptions),
                UsedFallback = true,
                FallbackReason = "未配置模型路径",
            };
        }

        if (!File.Exists(modelPath))
        {
            return new CreateResult
            {
                Predictor = new BaselinePredictor(aggregationOptions: aggregationOptions),
                UsedFallback = true,
                FallbackReason = $"模型文件不存在: {modelPath}",
            };
        }

        try
        {
            var predictor = new OnnxPredictor(modelPath, modelVersion, aggregationOptions);
            return new CreateResult { Predictor = predictor };
        }
        catch (Exception ex)
        {
            return new CreateResult
            {
                Predictor = new BaselinePredictor(aggregationOptions: aggregationOptions),
                UsedFallback = true,
                FallbackReason = $"模型加载失败: {ex.Message}",
            };
        }
    }
}
