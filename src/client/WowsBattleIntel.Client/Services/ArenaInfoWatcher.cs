using System.IO;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// tempArenaInfo.json 观测器：对局加载界面出现时立即捕获赛前名单。
/// </summary>
/// <remarks>
/// 游戏在加载界面把 tempArenaInfo.json 写进 replays 目录，对局结束后
/// 删除。本观测器轮询该文件，读到内容即触发事件，供界面展示赛前预测。
///
/// 用轮询而非 FileSystemWatcher，与 ReplayObserver 同一理由：游戏写文件
/// 期间可能多次变更，FileSystemWatcher 会提前触发且触发多次。轮询配
/// LastWriteTime 判重更可靠。
///
/// 读取采用 ApeRadar ArenaInfoReader 的口径：FileShare.ReadWrite 允许
/// 读游戏正在写的文件，读失败不视为异常，下一轮再试。
///
/// 常驻实例，配置变更走 <see cref="Rebind"/> 换配置，不换实例。事件订阅
/// 绑定在实例上，换实例会让界面收不到赛前名单通知，与
/// LiveBattleMonitor.Rebind 的设计理由相同。
/// </remarks>
public sealed class ArenaInfoWatcher : IDisposable
{
    /// <summary>残留名单时效窗口。文件修改时间距今超过该值视为游戏崩溃
    /// 残留或跨局遗留，不触发。ApeRadar requireFileToBeNewer 同语义。</summary>
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> knownKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object stateLock = new();
    private AppConfig config;
    private CancellationTokenSource? cts;
    private Task? loop;

    /// <summary>发现新的赛前名单时触发。参数为解析出的对局记录。</summary>
    public event Action<BattleRecord>? PreBattleDiscovered;

    /// <summary>
    /// 构造观测器。
    /// </summary>
    public ArenaInfoWatcher(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        this.config = config;
    }

    /// <summary>启动观测。重复调用无副作用。</summary>
    public void Start()
    {
        lock (stateLock)
        {
            if (loop is not null)
            {
                return;
            }

            cts = new CancellationTokenSource();
            loop = Task.Run(() => ObserveLoopAsync(cts.Token));
        }
    }

    /// <summary>停止观测。</summary>
    public void Stop()
    {
        CancellationTokenSource? toCancel;
        Task? toWait;
        lock (stateLock)
        {
            toCancel = cts;
            toWait = loop;
            loop = null;
            cts = null;
        }

        toCancel?.Cancel();
        try
        {
            toWait?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 停止超时不阻断退出。
        }

        toCancel?.Dispose();
    }

    /// <summary>
    /// 换绑配置。观测目录由配置派生，配置变更后必须换绑才生效。
    /// </summary>
    /// <remarks>
    /// 不换实例：保住事件订阅。运行中换绑自动恢复运行状态，与
    /// LiveBattleMonitor.Rebind 行为对齐。
    /// </remarks>
    public void Rebind(AppConfig newConfig)
    {
        ArgumentNullException.ThrowIfNull(newConfig);

        bool wasRunning;
        lock (stateLock)
        {
            wasRunning = loop is not null;
        }

        if (wasRunning)
        {
            Stop();
        }

        config = newConfig;
        knownKeys.Clear();

        if (wasRunning)
        {
            Start();
        }
    }

    /// <summary>
    /// 轮询循环。间隔取观测轮询配置的一半，赛前名单的时效要求更高。
    /// </summary>
    private async Task ObserveLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(
            Math.Max(2, config.PollIntervalSeconds / 2.0));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                ScanOnce();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 扫描异常不中断循环，下轮重试。
            }
        }
    }

    /// <summary>
    /// 扫描一次。命中判定：tempArenaInfo.json 存在、修改时间在时效窗口
    /// 内、解析成功。
    /// </summary>
    private void ScanOnce()
    {
        string replayRoot = config.ReplayDirectory;
        string arenaPath = Path.Combine(replayRoot, "tempArenaInfo.json");
        if (!File.Exists(arenaPath))
        {
            return;
        }

        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(arenaPath);
        }
        catch (IOException)
        {
            return;
        }

        // 崩溃残留或跨局遗留：修改时间太久远的文件不触发。游戏正常运行
        // 时该文件随对局生成，时效必然在窗口内。
        if (DateTime.UtcNow - lastWriteUtc > StaleThreshold)
        {
            return;
        }

        // 同一文件只在修改时间变化时重新解析，避免每轮重复触发。
        string key = $"{arenaPath}|{lastWriteUtc.Ticks}";
        if (!knownKeys.Add(key))
        {
            return;
        }

        if (!ArenaInfoParser.TryRead(arenaPath, out var arenaInfo) || arenaInfo is null)
        {
            // 读到写了一半的文件属正常，下轮 stamp 变了会重读。
            return;
        }

        var battle = ArenaInfoParser.ToBattleRecord(arenaInfo);
        if (battle.Players.Count == 0)
        {
            return;
        }

        PreBattleDiscovered?.Invoke(battle);
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
