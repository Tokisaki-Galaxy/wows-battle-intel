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
            // "未完成对局"是解析层的确定性判定（两个结果字段都为 null），
            // 用显式标记区分，界面据此选择中性提示与不重试的引导。
            if (ex.Message.Contains("未完成对局", StringComparison.Ordinal))
            {
                return IngestionResult.Unfinished();
            }

            return IngestionResult.Failed(ex.Message);
        }

        return await IngestParsedAsync(battle, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 对已解析的对局执行收敛判定、落库与预测。供 <see cref="IngestAsync"/> 与测试复用。
    /// </summary>
    internal async Task<IngestionResult> IngestParsedAsync(
        BattleRecord battle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(battle);

        // 同局收敛：同一局的战斗会被场上多个玩家的回放各解析一次，
        // battle_id 互不相同，按 battle_id 的幂等挡不住。这里按参战者
        // 集合、地图与时间窗识别等价对局，命中则跳过落库。
        var equivalent = await repository
            .FindEquivalentBattleAsync(battle, cancellationToken)
            .ConfigureAwait(false);
        if (equivalent is not null)
        {
            // 命中的可能是同一局的赛前占位记录（battle_id 带 :pre 后缀）。
            // 它只有名单与预测、没有战果，留在库里会让列表出现两条几乎相同
            // 的记录，那条还永远标着未判定。正式记录要重新落库，再清掉占位
            // 记录。
            if (equivalent.BattleId.EndsWith(":pre", StringComparison.Ordinal))
            {
                await repository.SaveBattleAsync(battle, cancellationToken).ConfigureAwait(false);
                if (repository is IBattleListProvider listProvider)
                {
                    await listProvider
                        .DeletePrebattlePlaceholdersAsync([equivalent.BattleId], cancellationToken)
                        .ConfigureAwait(false);
                }

                // 占位记录的目标标识由赛前名单算出，与正式标识不同，预测
                // 需要按正式标识重新生成，否则详情区读不到。
                return await SavePredictionAsync(battle, cancellationToken).ConfigureAwait(false);
            }

            return IngestionResult.Duplicate(battle, equivalent.BattleId);
        }

        // 落库。相同对局标识幂等。
        await repository.SaveBattleAsync(battle, cancellationToken).ConfigureAwait(false);
        return await SavePredictionAsync(battle, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 为已落库的对局生成并保存预测。
    /// </summary>
    private async Task<IngestionResult> SavePredictionAsync(
        BattleRecord battle,
        CancellationToken cancellationToken)
    {
        // 非 PvP 对局不出预测。Coop 全 bot、Unknown 无法定性，预测只会
        // 产出无意义数字，与训练端的类型白名单保持同一口径。
        if (!battle.BattleType.IsPvpType())
        {
            return IngestionResult.Succeeded(battle, $"非 PvP 对局不出预测（类型 {battle.BattleType}）");
        }

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

    /// <summary>是否因同局重复被跳过。跳过视为成功，但界面可区分提示。</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>
    /// 失败是否因为对局未完成（中途退出，游戏没写入战果）。
    /// </summary>
    /// <remarks>
    /// 这类失败是确定性的，重试永远同样结果，界面据此用中性提示而非
    /// 错误提示，文案也不同于网络或环境类失败。解析成功路径恒为 false。
    /// </remarks>
    public bool IsUnfinished { get; init; }

    /// <summary>解析出的对局，失败时为空。</summary>
    public BattleRecord? Battle { get; init; }

    /// <summary>失败或警告信息。</summary>
    public string? Message { get; init; }

    /// <summary>构造成功结果。</summary>
    public static IngestionResult Succeeded(BattleRecord battle, string? warning) =>
        new() { Success = true, Battle = battle, Message = warning };

    /// <summary>构造同局重复结果。与已有记录收敛，不再落库。</summary>
    public static IngestionResult Duplicate(BattleRecord battle, string existingBattleId) =>
        new()
        {
            Success = true,
            Battle = battle,
            IsDuplicate = true,
            Message = $"同局重复，已收敛到 {existingBattleId}",
        };

    /// <summary>构造失败结果。</summary>
    public static IngestionResult Failed(string message) =>
        new() { Success = false, Message = message };

    /// <summary>构造未完成对局的失败结果。与普通失败区分展示口径。</summary>
    public static IngestionResult Unfinished() =>
        new()
        {
            Success = false,
            IsUnfinished = true,
            Message = "该回放对应的对局中途退出，游戏没有写入战果记录",
        };
}