using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 历史回放补录：把回放目录里存在、库里还没有的对局补进库。
/// </summary>
/// <remarks>
/// 观测器只管“从此刻起新出现的回放”。早期实现首次扫描只登记不解析，启动前
/// 就躺在目录里的文件因此永远不会被处理，应用没在运行的那段时间打的每一局
/// 都收不进来，这是历史列表缺场次的根因。
///
/// 这里不再全量预解析：扫描只读文件名里的开战时间，比对库里的记录，把缺口
/// 作为占位项交给列表，用户点开某一局时才解析入库。启动不会被几百份回放
/// 拖住，缺的场次也能在列表里看见。
/// </remarks>
public sealed class ReplayBackfillService
{
    /// <summary>
    /// 文件名时间与库里开战时间的容差。
    /// </summary>
    /// <remarks>
    /// 文件名写到秒，解析结果也截到秒，但不同解析器的取整方式可能有细微
    /// 差别。留一分钟余量足以吸收这种偏差，又远小于正常对局间隔，不会把
    /// 相邻两局吞成一场。
    /// </remarks>
    private static readonly TimeSpan MatchTolerance = TimeSpan.FromMinutes(1);

    private readonly AppConfigAccessor config;
    private readonly IBattleListProvider listProvider;
    private readonly BattleIngestionService ingestion;
    private readonly Func<Task<IReadOnlyList<DateTimeOffset>>>? knownTimesProvider;
    private readonly LogService? logs;
    private readonly object knownTimesLock = new();
    private List<DateTimeOffset>? knownTimes;

    /// <summary>
    /// 构造补录服务。
    /// </summary>
    /// <param name="config">读取当前配置的委托，配置换绑后自动生效。</param>
    /// <param name="listProvider">列表读取入口，用于取库里已有的对局。</param>
    /// <param name="ingestion">入库服务，点开占位项时复用。</param>
    /// <param name="knownTimesProvider">
    /// 取库里全部开战时间。观测器首次扫描要逐份比对，几百份回放逐条查库
    /// 太慢，这里一次读进内存。
    /// </param>
    /// <param name="logs">运行日志。可为空。</param>
    public ReplayBackfillService(
        AppConfigAccessor config,
        IBattleListProvider listProvider,
        BattleIngestionService ingestion,
        Func<Task<IReadOnlyList<DateTimeOffset>>>? knownTimesProvider = null,
        LogService? logs = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(listProvider);
        ArgumentNullException.ThrowIfNull(ingestion);

        this.config = config;
        this.listProvider = listProvider;
        this.ingestion = ingestion;
        this.knownTimesProvider = knownTimesProvider;
        this.logs = logs;
    }

    /// <summary>
    /// 取当前配置的回放目录。
    /// </summary>
    /// <returns>回放目录，配置不可用时为空串。</returns>
    public string CurrentReplayRoot => config().ReplayDirectory;

    /// <summary>
    /// 建立一次列表快照：库里已有的条目，加上回放目录里还没有入库的占位项。
    /// </summary>
    /// <remarks>
    /// 库里的条目可能来自批量导入，硬盘上的回放已被游戏清掉，这类条目照常
    /// 列出；反过来硬盘上有、库里没有的，作为占位项一并返回。占位项只带
    /// 文件名里的开战时间，点开时才解析。
    /// </remarks>
    /// <param name="limit">库中条目上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<BattleListSnapshot> BuildSnapshotAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var stored = await listProvider.GetBattleListAsync(limit, cancellationToken)
            .ConfigureAwait(false);

        var merges = FindPlaceholderMerges(stored);
        if (merges.Count > 0)
        {
            // 同一局的赛前占位记录已能被正式记录代表，先从库里清掉再出快照。
            int removed = await listProvider
                .DeletePrebattlePlaceholdersAsync(merges, cancellationToken)
                .ConfigureAwait(false);
            if (removed > 0)
            {
                logs?.Info($"清理赛前占位记录 {removed} 条，同局已有正式记录");
            }

            stored = await listProvider.GetBattleListAsync(limit, cancellationToken)
                .ConfigureAwait(false);
        }

        // 回放扫描在后台线程做，目录大时也不卡界面。
        var replays = await Task.Run(
            () => ReplayDirectoryScanner.Scan(
                CurrentReplayRoot,
                TimeSpan.FromSeconds(5),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var pending = new List<ReplayFileInfo>();
        foreach (var replay in replays)
        {
            if (!IsRepresented(replay, stored))
            {
                pending.Add(replay);
            }
        }

        return new BattleListSnapshot(stored, pending);
    }

    /// <summary>
    /// 判断某开战时间是否已有库记录代表。第一次调用时把库里全部开战时间
    /// 读进内存，后续逐份比对不再查库。
    /// </summary>
    /// <param name="startedAt">回放文件名里的开战时间。</param>
    public async Task<bool> IsKnownAsync(DateTimeOffset startedAt)
    {
        var known = await EnsureKnownTimesAsync().ConfigureAwait(false);
        return known.Any(t => (t - startedAt).Duration() <= MatchTolerance);
    }

    /// <summary>
    /// 解析一份占位回放并入库。
    /// </summary>
    /// <param name="replayPath">回放文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>入库结果，失败时 Success 为 false 并带原因。</returns>
    public async Task<IngestionResult> IngestAsync(
        string replayPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayPath);

        var result = await ingestion.IngestAsync(replayPath, cancellationToken)
            .ConfigureAwait(false);
        if (result.Success && result.Battle is not null)
        {
            // 新入库的时间记进缓存，后续比对不必整表重读。
            lock (knownTimesLock)
            {
                knownTimes?.Add(result.Battle.StartedAt);
            }
        }

        return result;
    }

    /// <summary>
    /// 加载并缓存库里全部开战时间。缓存活一个进程周期，期间新入库的对局
    /// 由 <see cref="IngestAsync"/> 补记。
    /// </summary>
    private async Task<List<DateTimeOffset>> EnsureKnownTimesAsync()
    {
        lock (knownTimesLock)
        {
            if (knownTimes is { } cached)
            {
                return cached;
            }
        }

        var list = knownTimesProvider is null
            ? new List<DateTimeOffset>()
            : (await knownTimesProvider().ConfigureAwait(false)).ToList();

        lock (knownTimesLock)
        {
            knownTimes = list;
        }

        return list;
    }

    /// <summary>
    /// 判断一份回放是否已有库记录代表。
    /// </summary>
    /// <remarks>
    /// 按开战时间比对：库里存在容差内的条目即认为已录入。占位记录的地图为空、
    /// 时间来自 tempArenaInfo，同样按时间匹配。
    /// </remarks>
    private static bool IsRepresented(ReplayFileInfo replay, IReadOnlyList<BattleListEntry> stored)
    {
        foreach (var entry in stored)
        {
            if ((entry.StartedAt - replay.StartedAt).Duration() <= MatchTolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 找出可以删除的赛前占位记录。
    /// </summary>
    /// <remarks>
    /// 占位记录与正式记录必然开战时间相近。库里如果同一时间窗内同时存在
    /// 占位记录与正式记录，占位记录就是多余的。只删占位记录，正式记录一条不动。
    /// </remarks>
    private static List<string> FindPlaceholderMerges(IReadOnlyList<BattleListEntry> stored)
    {
        var formal = stored.Where(e => !e.IsPrebattle).ToList();
        var merges = new List<string>();
        foreach (var placeholder in stored.Where(e => e.IsPrebattle))
        {
            bool covered = formal.Any(e =>
                (e.StartedAt - placeholder.StartedAt).Duration() <= MatchTolerance);
            if (covered)
            {
                merges.Add(placeholder.BattleId);
            }
        }

        return merges;
    }
}

/// <summary>
/// 配置读取委托。配置会随设置页换绑，服务持有委托而不是配置实例，
/// 换绑后自然读到新目录。
/// </summary>
/// <returns>当前配置。</returns>
public delegate AppConfig AppConfigAccessor();
