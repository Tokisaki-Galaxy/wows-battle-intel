using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Replay.Dtos;

namespace WowsBattleIntel.Replay;

/// <summary>
/// 通过外部 replayshark 进程解析回放。
/// </summary>
/// <remarks>
/// 选择外部进程而非进程内解析，理由见 ADR 0004：零移植成本，版本兼容
/// 由 wows-toolkit 维护。进程调用的细节被本类封装，将来换进程内实现时
/// 上层代码不受影响。
///
/// replayshark 用 -g 直接指向游戏安装目录，自行从 res_packages 下的 pkg
/// 里读 .def 定义与 GameParams。这样不再需要预先用 wowsunpack 提取数据，
/// 地图名与舰名也能得到可直接展示的名称而非内部 id。
///
/// 调用参数：
/// - -g 游戏安装目录
/// - battle-results 子命令加 --out-file
///
/// -c constants.json 在 -g 模式下可选。实测缺失也能正常产出结果，因此不再传。
/// </remarks>
public sealed class ReplaysharkReplayParser : IReplayParser
{
    private readonly ReplaysharkOptions options;

    /// <summary>
    /// 构造解析器。
    /// </summary>
    /// <param name="options">解析器配置，含可执行文件与数据目录路径。</param>
    public ReplaysharkReplayParser(ReplaysharkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
    }

    /// <inheritdoc />
    public string Name => "replayshark";

    /// <inheritdoc />
    /// <remarks>
    /// 回放文件的版本校验由 replayshark 自身完成，它会比对回放 build 号
    /// 与数据目录的 metadata.toml。这里只做文件存在性与扩展名检查，
    /// 版本不匹配的失败留到实际解析时处理。
    /// </remarks>
    public Task<bool> CanParseAsync(string replayPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(replayPath) || !File.Exists(replayPath))
        {
            return Task.FromResult(false);
        }

        bool isReplay = replayPath.EndsWith(".wowsreplay", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(isReplay);
    }

    /// <inheritdoc />
    public async Task<BattleRecord> ParseAsync(
        string replayPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayPath);

        if (!File.Exists(replayPath))
        {
            throw new ReplayParseException($"回放文件不存在: {replayPath}");
        }

        string tempOutput = Path.Combine(
            Path.GetTempPath(),
            $"wbi-{Guid.NewGuid():N}.json");

        try
        {
            await RunReplaysharkAsync(replayPath, tempOutput, cancellationToken)
                .ConfigureAwait(false);

            if (!File.Exists(tempOutput))
            {
                throw new ReplayParseException(
                    $"replayshark 未产出输出文件，可能是版本不匹配或数据目录缺失: {Path.GetFileName(replayPath)}");
            }

            string json = await File.ReadAllTextAsync(tempOutput, cancellationToken)
                .ConfigureAwait(false);
            var output = JsonConvert.DeserializeObject<ReplaysharkOutput>(json);

            if (output is null)
            {
                throw new ReplayParseException($"解析 replayshark 输出失败: {Path.GetFileName(replayPath)}");
            }

            var battle = ReplaysharkMapper.Map(output, replayPath);
            if (battle is null)
            {
                throw new ReplayParseException(
                    $"回放不含完整战斗结果，可能是未完成对局: {Path.GetFileName(replayPath)}");
            }

            return battle;
        }
        finally
        {
            TryDelete(tempOutput);
        }
    }

    /// <summary>
    /// 调用 replayshark 进程。
    /// </summary>
    private async Task RunReplaysharkAsync(
        string replayPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = options.ReplaysharkPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // 全局参数必须放在子命令之前。
        psi.ArgumentList.Add("-g");
        psi.ArgumentList.Add(options.GamePath);

        if (!string.IsNullOrWhiteSpace(options.ConstantsPath))
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(options.ConstantsPath);
        }

        psi.ArgumentList.Add("battle-results");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("normalized");
        psi.ArgumentList.Add("--out-file");
        psi.ArgumentList.Add(outputPath);
        psi.ArgumentList.Add(replayPath);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.ParseTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new ReplayParseException(
                    $"replayshark 解析超时（{options.ParseTimeout.TotalSeconds:F0} 秒）: {Path.GetFileName(replayPath)}");
            }
        }
        catch (Exception ex) when (ex is not ReplayParseException and not OperationCanceledException)
        {
            throw new ReplayParseException(
                $"启动 replayshark 失败，请检查路径配置: {options.ReplaysharkPath}", ex);
        }

        if (process.ExitCode != 0)
        {
            throw new ReplayParseException(
                $"replayshark 退出码 {process.ExitCode}: {stderr.ToString().Trim()}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程已退出或无法终止，忽略。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件残留不影响功能。
        }
    }
}

/// <summary>
/// replayshark 解析器配置。
/// </summary>
public sealed class ReplaysharkOptions
{
    /// <summary>replayshark 可执行文件路径。</summary>
    public string ReplaysharkPath { get; set; } = "replayshark.exe";

    /// <summary>
    /// 游戏安装目录，replayshark 据此从 res_packages 读取定义与 GameParams。
    /// </summary>
    public string GamePath { get; set; } = string.Empty;

    /// <summary>
    /// constants.json 路径，可选。为 -g 模式下的补充覆盖，留空时由 replayshark
    /// 自行从游戏数据里取。
    /// </summary>
    public string ConstantsPath { get; set; } = string.Empty;

    /// <summary>单份回放的解析超时，默认 60 秒。</summary>
    public TimeSpan ParseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>校验必要配置。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ReplaysharkPath))
        {
            throw new ArgumentException("必须指定 replayshark 可执行文件路径。", nameof(ReplaysharkPath));
        }

        if (string.IsNullOrWhiteSpace(GamePath))
        {
            throw new ArgumentException("必须指定游戏安装目录。", nameof(GamePath));
        }
    }
}
