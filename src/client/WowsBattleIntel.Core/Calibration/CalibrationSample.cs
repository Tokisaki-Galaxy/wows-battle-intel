using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Calibration;

/// <summary>
/// 一条参与校准的样本：一次赛前预测与它事后回填的真实结果。
/// </summary>
/// <remarks>
/// 单独建模而不是用元组，因为它是校准统计的输入契约。仓储按此形状返回，
/// 校准报告直接消费，两端共用同一类型可避免字段顺序错位。
/// </remarks>
/// <param name="Prediction">赛前预测记录。</param>
/// <param name="Outcome">赛后回填的真实结果。</param>
public sealed record CalibrationSample(PredictionResult Prediction, BattleOutcome Outcome);
