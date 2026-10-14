using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Core.Abstractions;

/// <summary>
/// 胜率预测器。输入战斗，输出双方胜率期望。
/// </summary>
/// <remarks>
/// 接口刻意保持窄，便于用固定输出的替身做测试。
/// 实现分两类：朴素基线（规则或逻辑回归）与训练好的模型（ONNX 推理）。
/// </remarks>
public interface IPredictor
{
    /// <summary>模型版本标识，写入预测记录用于追溯。</summary>
    string ModelVersion { get; }

    /// <summary>模型文件哈希，空字符串表示无模型文件。</summary>
    string ModelHash { get; }

    /// <summary>
    /// 对一场战斗做赛前预测。
    /// </summary>
    /// <param name="battle">战斗记录，含双方玩家与特征。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<PredictionResult> PredictAsync(
        BattleRecord battle,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 持久化战斗与预测记录。
/// </summary>
/// <remarks>
/// 本地与云端各自实现这个接口。云端起步用 D1，规模触顶时换 Neon，
/// 业务代码零改动。这是"持久层可替换"原则的落点。
/// </remarks>
public interface IBattleRepository
{
    /// <summary>保存一场战斗，幂等。相同 BattleId 重复写入不产生重复记录。</summary>
    Task SaveBattleAsync(BattleRecord battle, CancellationToken cancellationToken = default);

    /// <summary>保存一条预测记录。</summary>
    Task SavePredictionAsync(PredictionResult prediction, CancellationToken cancellationToken = default);

    /// <summary>按对局标识读取战斗。</summary>
    Task<BattleRecord?> GetBattleAsync(string battleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 回填某局预测的真实结果，用于事后校准。
    /// </summary>
    Task BackfillOutcomeAsync(
        string battleId,
        BattleOutcome outcome,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取出尚未参与校准、且已有真实结果的预测，供校准统计消费。
    /// </summary>
    Task<IReadOnlyList<PredictionResult>> GetUncalibratedPredictionsAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
