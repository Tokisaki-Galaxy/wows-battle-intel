using System.IO;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Scoring;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// tempArenaInfo.json 观测器：对局加载界面出现时立即捕获赛前名单。
/// </summary>
/// <remarks>
/// 逐行对齐 ApeRadar 的实时链路（ArenaInfoReader + 主窗口 1 秒定时器）：
/// 每秒在游戏 replays 目录下递归找 tempArenaInfo.json，取修改时间最新者，
/// 只要比上次见到的时间更新就触发一次。没有任何时效窗口，客户端在对局
/// 中途启动也能立刻看到正在进行的这一局。
///
/// 游戏在加载界面写入该文件，玩家陆续加入时多次重写，对局结束后删除。
/// 文件从"存在且已触发"变为"不存在"时触发 <see cref="PreBattleEnded"/>，
/// 让界面收起当前对局。
///
/// 读取采用 ApeRadar ArenaInfoReader 的口径：FileShare.ReadWrite 允许读
/// 游戏正在写的文件，读失败不视为异常，下一轮再试。
///
/// 常驻实例，配置变更走 <see cref="Rebind"/> 换配置，不换实例。事件订阅
/// 绑定在实例上，换实例会让界面收不到赛前名单通知，与
/// LiveBattleMonitor.Rebind 的设计理由相同。
/// </remarks>
public sealed class ArenaInfoWatcher : IDisposable
{
    private readonly object stateLock = new();
    private AppConfig config;
    private CancellationTokenSource? cts;
    private Task? loop;

    /// <summary>上次成功触发时该文件的修改时间，用于"比上次新才触发"判重。</summary>
    private DateTime lastSeenWriteTimeUtc = DateTime.MinValue;

    /// <summary>当前是否已有一局在展示中，用于识别对局结束。</summary>
    private bool battleActive;

    /// <summary>发现新的赛前名单时触发。参数为解析出的对局记录。</summary>
    public event Action<BattleRecord>? PreBattleDiscovered;

    /// <summary>当前对局结束（tempArenaInfo 被游戏删除）时触发。</summary>
    public event Action? PreBattleEnded;

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
        lastSeenWriteTimeUtc = DateTime.MinValue;
        battleActive = false;

        if (wasRunning)
        {
            Start();
        }
    }

    /// <summary>
    /// 轮询循环。1 秒一轮，与 ApeRadar 主窗口定时器同频。
    /// </summary>
    private async Task ObserveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ScanOnce();
            }
            catch
            {
                // 扫描异常不中断循环，下轮重试。
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 扫描一次：递归找最新的 tempArenaInfo.json，比上次新则触发；
    /// 上一轮有对局而本轮文件消失则判定对局结束。
    /// </summary>
    /// <remarks>internal 供单元测试直接驱动，避免依赖后台定时循环。</remarks>
    internal void ScanOnce()
    {
        string? latestPath;
        try
        {
            latestPath = FindLatestArenaInfoFile();
        }
        catch (Exception)
        {
            // 目录枚举失败（权限或瞬时 IO）不等于对局结束，本轮不改变状态。
            return;
        }

        if (latestPath is null)
        {
            if (battleActive)
            {
                battleActive = false;
                lastSeenWriteTimeUtc = DateTime.MinValue;
                PreBattleEnded?.Invoke();
            }

            return;
        }

        DateTime writeTimeUtc;
        try
        {
            writeTimeUtc = File.GetLastWriteTimeUtc(latestPath);
        }
        catch (IOException)
        {
            return;
        }

        // 与上次触发的时间相同或更旧：文件未变，不重复触发。
        if (writeTimeUtc <= lastSeenWriteTimeUtc)
        {
            return;
        }

        if (!ArenaInfoParser.TryRead(latestPath, out var arenaInfo) || arenaInfo is null)
        {
            // 读到写了一半的文件属正常，下轮 stamp 变了会重读。
            return;
        }

        var battle = ArenaInfoParser.ToBattleRecord(arenaInfo);
        if (battle.Players.Count == 0)
        {
            return;
        }

        // 服务器兜底：tempArenaInfo 无 realm，从游戏日志探测。探测不到时
        // 保持 Auto，由调用方决定是否跳过。
        if (battle.Server == GameServer.Auto)
        {
            var server = ServerResolver.Resolve(config.GamePath);
            if (server != GameServer.Auto)
            {
                battle.Server = server;
                foreach (var player in battle.Players)
                {
                    player.Server = server;
                }
            }
        }

        lastSeenWriteTimeUtc = writeTimeUtc;
        battleActive = true;
        PreBattleDiscovered?.Invoke(battle);
    }

    /// <summary>
    /// 在游戏 replays 目录下递归找修改时间最新的 tempArenaInfo.json。
    /// 目录不存在返回 null；枚举过程中的异常向上抛，由调用方区分
    /// "确实没有文件"与"读取失败"。
    /// </summary>
    private string? FindLatestArenaInfoFile()
    {
        string replayRoot = config.ReplayDirectory;
        if (!Directory.Exists(replayRoot))
        {
            return null;
        }

        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string path in Directory.EnumerateFiles(
            replayRoot, "tempArenaInfo.json", SearchOption.AllDirectories))
        {
            DateTime wrote;
            try
            {
                wrote = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                continue;
            }

            if (wrote > bestTime)
            {
                bestTime = wrote;
                best = path;
            }
        }

        return best;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
