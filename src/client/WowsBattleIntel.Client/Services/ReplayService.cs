using System.IO;
using WowsBattleIntel.Client.Configuration;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Replay;

namespace WowsBattleIntel.Client.Services;

/// <summary>
/// 回放解析服务。
/// </summary>
/// <remarks>
/// 直接用游戏安装目录作为数据源：replayshark 的 -g 参数会从游戏自己的
/// res_packages 里读定义与 GameParams，因此不再需要预提取的 gamedata，
/// 也不必按回放版本挑目录。
///
/// 代价是解析依赖本机装了游戏本体。本工具本来就要读游戏目录下的
/// replays 做实时观测，这个前提天然成立。
/// </remarks>
public sealed class ReplayService
{
    private readonly AppConfig config;

    /// <summary>
    /// 构造服务。
    /// </summary>
    public ReplayService(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        this.config = config;
    }

    /// <summary>
    /// 解析单个回放。
    /// </summary>
    /// <param name="replayPath">回放文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>战斗记录。版本不匹配或未完成对局时抛异常。</returns>
    public async Task<BattleRecord> ParseAsync(
        string replayPath,
        CancellationToken cancellationToken = default)
    {
        var parser = CreateParser(replayPath);
        return await parser.ParseAsync(replayPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 判断回放是否可解析，即游戏目录是否就绪、文件是否像回放。
    /// </summary>
    public bool CanParse(string replayPath)
    {
        if (!File.Exists(replayPath)
            || !replayPath.EndsWith(".wowsreplay", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(config.GamePath) && Directory.Exists(config.GamePath);
    }

    /// <summary>
    /// 为指定回放创建解析器。数据源固定为游戏安装目录。
    /// </summary>
    private ReplaysharkReplayParser CreateParser(string replayPath)
    {
        if (string.IsNullOrWhiteSpace(config.GamePath) || !Directory.Exists(config.GamePath))
        {
            throw new ReplayParseException(
                $"找不到游戏安装目录，无法解析回放。请在设置页确认游戏目录。\n回放: {Path.GetFileName(replayPath)}");
        }

        var options = new ReplaysharkOptions
        {
            ReplaysharkPath = config.ReplaysharkPath,
            GamePath = config.GamePath,
        };

        return new ReplaysharkReplayParser(options);
    }
}
