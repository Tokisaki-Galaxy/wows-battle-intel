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
/// 封装版本匹配逻辑：回放、gamedata、constants 三者版本必须一致。
/// 服务根据回放所在目录名推断版本，选取对应的数据目录与 constants，
/// 再委托给 ReplaysharkReplayParser。
///
/// 游戏更新后回放目录会新增版本子目录，服务自动适配，无需改代码。
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
    /// 判断回放是否可解析，即所需版本数据是否齐备。
    /// </summary>
    public bool CanParse(string replayPath)
    {
        try
        {
            CreateParser(replayPath);
            return File.Exists(replayPath)
                && replayPath.EndsWith(".wowsreplay", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 为指定回放创建配置好版本的解析器。
    /// </summary>
    private ReplaysharkReplayParser CreateParser(string replayPath)
    {
        string? extractedDir = ResolveExtractedDir(replayPath);
        if (extractedDir is null)
        {
            throw new ReplayParseException(
                $"找不到回放对应的游戏数据目录。请确认 data/gamedata 下已准备该版本的数据。\n回放: {Path.GetFileName(replayPath)}");
        }

        string? constantsFile = ResolveConstantsFile(extractedDir);
        if (constantsFile is null)
        {
            throw new ReplayParseException(
                $"找不到对应的 constants.json。请确认 data/constants 下已准备该版本。\n版本目录: {Path.GetFileName(extractedDir)}");
        }

        var options = new ReplaysharkOptions
        {
            ReplaysharkPath = config.ReplaysharkPath,
            ExtractedDataPath = extractedDir,
            ConstantsPath = constantsFile,
        };

        return new ReplaysharkReplayParser(options);
    }

    /// <summary>
    /// 根据回放路径推断版本，选出对应的 gamedata 目录。
    /// </summary>
    /// <remarks>
    /// 先从回放所在目录名提取版本（如 15.6.0.0），转成 gamedata 的前缀
    /// （如 15.6.0）后匹配。目录名不含版本时，退化为唯一可用版本。
    /// </remarks>
    private string? ResolveExtractedDir(string replayPath)
    {
        if (string.IsNullOrWhiteSpace(config.ExtractedDataPath)
            || !Directory.Exists(config.ExtractedDataPath))
        {
            return null;
        }

        var versionDirs = Directory.GetDirectories(config.ExtractedDataPath)
            .Where(d => !Path.GetFileName(d).Equals("common", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (versionDirs.Count == 0)
        {
            return null;
        }

        // 从回放路径中提取四段式版本号。
        string? version = ExtractVersion(replayPath);
        if (version is not null)
        {
            // 15.6.0.0 -> 15.6.0
            string shortVersion = version.EndsWith(".0", StringComparison.Ordinal)
                ? version[..^2]
                : version;

            var match = versionDirs.FirstOrDefault(
                d => Path.GetFileName(d).StartsWith(shortVersion, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        // 推断失败时，若只有一个版本则直接用。
        return versionDirs.Count == 1 ? versionDirs[0] : null;
    }

    /// <summary>
    /// 找到 gamedata 版本目录对应的 constants.json。
    /// </summary>
    private string? ResolveConstantsFile(string extractedDir)
    {
        if (string.IsNullOrWhiteSpace(config.ConstantsRootPath)
            || !Directory.Exists(config.ConstantsRootPath))
        {
            return null;
        }

        string versionName = Path.GetFileName(extractedDir);
        string direct = Path.Combine(config.ConstantsRootPath, versionName, "constants.json");
        if (File.Exists(direct))
        {
            return direct;
        }

        // 兜底：递归查找同名版本目录。
        return Directory.GetFiles(config.ConstantsRootPath, "constants.json", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Contains(versionName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 从路径中提取四段式版本号，如 15.6.0.0。
    /// </summary>
    internal static string? ExtractVersion(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            path, @"\b(\d+\.\d+\.\d+\.\d+)\b");
        return match.Success ? match.Groups[1].Value : null;
    }
}
