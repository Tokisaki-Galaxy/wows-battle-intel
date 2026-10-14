using System.IO;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 战斗入库服务。
/// </summary>
/// <remarks>
/// 把"观测到回放 → 解析 → 落库 → 预测记录"这条链路串起来。
/// 这是数据闭环的核心：每局回放解析后产出标签，落进本地库，
/// 累积成训练与校准的数据基础。
/// </remarks>
public sealed class BattleIngestionService
{
    private readonly ReplayService replayService;
    private readonly IBattleRepository repository;
    private readonly IPredictor predictor;

    /// <summary>
    /// 构造服务。
    /// </summary>
    public BattleIngestionService(
        ReplayService replayService,
        IBattleRepository repository,
        IPredictor predictor)
    {
        ArgumentNullException.ThrowIfNull(replayService);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(predictor);

        this.replayService = replayService;
        this.repository = repository;
        this.predictor = predictor;
    }

    /// <summary>
    /// 处理一份回放：解析、落库、生成预测记录并回填结果。
    /// </summary>
    /// <param name="replayPath">回放文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>处理结果。</returns>
    public async Task<IngestionResult> IngestAsync(
        string replayPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayPath);

        if (!File.Exists(replayPath))
        {
            return IngestionResult.Failed($"文件不存在: {replayPath}");
        }

        BattleRecord battle;
        try
        {
            battle = await replayService.ParseAsync(replayPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ReplayParseException ex)
        {
            return IngestionResult.Failed(ex.Message);
        }

        // 落库。相同对局标识幂等。
        await repository.SaveBattleAsync(battle, cancellationToken).ConfigureAwait(false);

        // 生成预测记录。此时真实结果已从回放得知，但预测器不使用它，
        // 特征仅来自赛前可见的统计数据。
        PredictionResult prediction;
        try
        {
            prediction = await predictor.PredictAsync(battle, cancellationToken)
                .ConfigureAwait(false);
            await repository.SavePredictionAsync(prediction, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 预测失败不影响对局落库，二者解耦。
            return IngestionResult.Succeeded(battle, $"预测失败: {ex.Message}");
        }

        return IngestionResult.Succeeded(battle, null);
    }

    /// <summary>
    /// 批量处理一个目录下的回放。
    /// </summary>
    public async Task<(int Succeeded, int Failed)> IngestDirectoryAsync(
        string directory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        var files = Directory.GetFiles(directory, "*.wowsreplay", SearchOption.AllDirectories);
        int succeeded = 0;
        int failed = 0;

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await IngestAsync(file, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }

            progress?.Report($"[{succeeded + failed}/{files.Length}] {Path.GetFileName(file)}");
        }

        return (succeeded, failed);
    }
}

/// <summary>
/// 单份回放的入库结果。
/// </summary>
public sealed class IngestionResult
{
    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>解析出的对局，失败时为空。</summary>
    public BattleRecord? Battle { get; init; }

    /// <summary>失败或警告信息。</summary>
    public string? Message { get; init; }

    /// <summary>构造成功结果。</summary>
    public static IngestionResult Succeeded(BattleRecord battle, string? warning) =>
        new() { Success = true, Battle = battle, Message = warning };

    /// <summary>构造失败结果。</summary>
    public static IngestionResult Failed(string message) =>
        new() { Success = false, Message = message };
}
