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
/// </remarks>
public sealed class ArenaInfoWatcher : IDisposable
{
    private readonly AppConfig config;
    private readonly HashSet<string> knownKeys = new(StringComparer.OrdinalIgnoreCase);
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
        if (loop is not null)
        {
            return;
        }

        cts = new CancellationTokenSource();
        loop = Task.Run(() => ObserveLoopAsync(cts.Token));
    }

    /// <summary>停止观测。</summary>
    public void Stop()
    {
        cts?.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 停止超时不阻断退出。
        }

        loop = null;
        cts?.Dispose();
        cts = null;
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
    /// 扫描一次。命中判定：replays 目录存在 tempArenaInfo.json，解析
    /// 成功且参战者指纹未见过时触发。
    /// </summary>
    private void ScanOnce()
    {
        string replayRoot = config.ReplayDirectory;
        string arenaPath = Path.Combine(replayRoot, "tempArenaInfo.json");
        if (!File.Exists(arenaPath))
        {
            return;
        }

        // 同一文件只在修改时间变化时重新解析，避免每轮重复触发。
        string? stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(arenaPath).Ticks.ToString();
        }
        catch (IOException)
        {
            return;
        }

        string key = $"{arenaPath}|{stamp}";
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
