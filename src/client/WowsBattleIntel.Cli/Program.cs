using System.Globalization;
using Newtonsoft.Json;
using WowsBattleIntel.Core.Calibration;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Prediction;
using WowsBattleIntel.Replay;
using WowsBattleIntel.Replay.Dtos;

namespace WowsBattleIntel.Cli;

/// <summary>
/// 命令行入口，用于端到端验证与离线分析。
/// </summary>
/// <remarks>
/// 子命令：
/// - map：把一份 replayshark 输出映射成领域模型并打印摘要
/// - backtest：对一批已解析的回放跑预测与校准，输出 Brier 分数与校准曲线
/// - survey：统计一批已解析回放的元信息分布
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "map" => RunMap(args),
                "backtest" => RunBacktest(args),
                "survey" => RunSurvey(args),
                "help" or "--help" or "-h" => PrintUsageAndSucceed(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// map 子命令：单份输出映射为领域模型并打印摘要。
    /// </summary>
    private static int RunMap(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: wows-intel map <replayshark_output.json>");
            return 1;
        }

        string path = args[1];
        var output = LoadOutput(path);
        var battle = ReplaysharkMapper.Map(output, path);

        if (battle is null)
        {
            Console.WriteLine("该回放不含完整战斗结果，跳过。");
            return 0;
        }

        Console.WriteLine($"对局标识: {battle.BattleId}");
        Console.WriteLine($"开始时间: {battle.StartedAt:u}");
        Console.WriteLine($"地图: {battle.MapId}");
        Console.WriteLine($"类型: {battle.BattleType}");
        Console.WriteLine($"版本: {battle.GameVersion}");
        Console.WriteLine($"服务器: {battle.Server}");
        Console.WriteLine($"结果: {battle.Outcome}");
        Console.WriteLine($"时长: {battle.DurationSeconds} 秒");
        Console.WriteLine($"参战者: {battle.Players.Count} 人 (友 {battle.Allies.Count()} / 敌 {battle.Enemies.Count()})");
        Console.WriteLine();

        PrintTeam("友方", battle.Allies);
        PrintTeam("敌方", battle.Enemies);

        return 0;
    }

    private static void PrintTeam(string label, IEnumerable<PlayerRecord> players)
    {
        Console.WriteLine($"=== {label} ===");
        Console.WriteLine($"{"昵称",-20} {"公会",-8} {"舰种",-12} {"等级",4} {"组队",4}");
        foreach (var p in players)
        {
            Console.WriteLine($"{p.PlayerName,-20} {p.ClanTag,-8} {p.ShipClass,-12} {p.ShipTier,4} {p.DivisionSize,4}");
        }
        Console.WriteLine();
    }

    /// <summary>
    /// backtest 子命令：对一批已解析回放跑预测与校准。
    /// </summary>
    private static int RunBacktest(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: wows-intel backtest <输出目录> [--csv <结果文件>]");
            return 1;
        }

        string dir = args[1];
        string? csvPath = null;
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--csv")
            {
                csvPath = args[i + 1];
            }
        }

        var files = Directory.GetFiles(dir, "*.json");
        Console.WriteLine($"扫描到 {files.Count()} 份解析结果");

        var predictor = new BaselinePredictor();
        var pairs = new List<(PredictionResult Prediction, BattleOutcome Outcome)>();
        var rows = new List<BacktestRow>();
        int skipped = 0;

        foreach (string file in files)
        {
            ReplaysharkOutput output;
            try
            {
                output = LoadOutput(file);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  跳过 {Path.GetFileName(file)}: {ex.Message}");
                skipped++;
                continue;
            }

            var battle = ReplaysharkMapper.Map(output, file);
            if (battle is null)
            {
                skipped++;
                continue;
            }

            var prediction = predictor.PredictAsync(battle).GetAwaiter().GetResult();
            pairs.Add((prediction, battle.Outcome));

            rows.Add(new BacktestRow
            {
                BattleId = battle.BattleId,
                File = Path.GetFileName(file),
                Predicted = prediction.AllyWinProbability,
                Actual = battle.Outcome == BattleOutcome.Win ? 1.0 : 0.0,
                Outcome = battle.Outcome.ToString(),
                Confidence = prediction.Confidence,
                AllyCount = battle.Allies.Count(),
            });
        }

        if (pairs.Count == 0)
        {
            Console.WriteLine("没有可用于校准的样本。");
            return 0;
        }

        var report = CalibrationMetrics.BuildReport(pairs);

        Console.WriteLine();
        Console.WriteLine("=== 基线预测器回测 ===");
        Console.WriteLine($"样本数: {report.SampleCount}");
        Console.WriteLine($"Brier 分数: {report.BrierScore:F4}  (0 为完美, 0.25 为无信息基线)");
        Console.WriteLine($"对数损失:   {report.LogLoss:F4}");
        Console.WriteLine($"判别准确率: {report.Accuracy:P2}");
        Console.WriteLine();

        Console.WriteLine("=== 校准曲线 ===");
        Console.WriteLine($"{"概率区间",-16} {"样本",6} {"预测均值",10} {"实际胜率",10} {"偏差",10}");
        foreach (var bucket in report.Curve)
        {
            string range = $"{bucket.LowerBound:F1}-{bucket.UpperBound:F1}";
            Console.WriteLine(
                $"{range,-16} {bucket.SampleCount,6} {bucket.PredictedMean,10:F3} {bucket.ActualWinrate,10:F3} {bucket.CalibrationError,10:+0.000;-0.000}");
        }

        if (csvPath is not null)
        {
            WriteBacktestCsv(csvPath, rows);
            Console.WriteLine();
            Console.WriteLine($"明细已写入: {csvPath}");
        }

        return 0;
    }

    /// <summary>
    /// survey 子命令：统计一批解析结果的分布。
    /// </summary>
    private static int RunSurvey(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: wows-intel survey <输出目录>");
            return 1;
        }

        string dir = args[1];
        var files = Directory.GetFiles(dir, "*.json");

        var typeCounts = new Dictionary<string, int>();
        var resultCounts = new Dictionary<string, int>();
        int total = 0;
        int withResult = 0;
        int playerSum = 0;

        foreach (string file in files)
        {
            ReplaysharkOutput output;
            try
            {
                output = LoadOutput(file);
            }
            catch
            {
                continue;
            }

            total++;
            string type = output.Metadata.GameType;
            typeCounts[type] = typeCounts.GetValueOrDefault(type) + 1;

            string result = output.Metadata.BattleResult?.Type ?? "无";
            resultCounts[result] = resultCounts.GetValueOrDefault(result) + 1;
            if (output.Metadata.BattleResult is not null)
            {
                withResult++;
            }

            playerSum += output.Players.Count;
        }

        Console.WriteLine($"解析文件总数: {total}");
        Console.WriteLine($"含战斗结果: {withResult}");
        Console.WriteLine($"平均人数: {(total > 0 ? (double)playerSum / total : 0):F1}");
        Console.WriteLine();
        Console.WriteLine("按对局类型:");
        foreach (var (key, count) in typeCounts.OrderByDescending(x => x.Value))
        {
            Console.WriteLine($"  {key,-20} {count,5}");
        }
        Console.WriteLine();
        Console.WriteLine("按战斗结果:");
        foreach (var (key, count) in resultCounts.OrderByDescending(x => x.Value))
        {
            Console.WriteLine($"  {key,-20} {count,5}");
        }

        return 0;
    }

    private static ReplaysharkOutput LoadOutput(string path)
    {
        string json = File.ReadAllText(path);
        return JsonConvert.DeserializeObject<ReplaysharkOutput>(json)
            ?? throw new InvalidOperationException($"反序列化失败: {path}");
    }

    private static void WriteBacktestCsv(string path, IEnumerable<BacktestRow> rows)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("battle_id,file,predicted,actual,outcome,confidence,ally_count");
        foreach (var r in rows)
        {
            writer.WriteLine(
                $"{r.BattleId},{r.File},{r.Predicted.ToString("F6", CultureInfo.InvariantCulture)}," +
                $"{r.Actual.ToString("F0", CultureInfo.InvariantCulture)},{r.Outcome}," +
                $"{r.Confidence.ToString("F4", CultureInfo.InvariantCulture)},{r.AllyCount}");
        }
    }

    private static int PrintUsageAndSucceed()
    {
        PrintUsage();
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知子命令: {cmd}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("wows-intel 端到端验证与离线分析工具");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  wows-intel map <replayshark_output.json>     单份映射为领域模型");
        Console.WriteLine("  wows-intel backtest <目录> [--csv <文件>]    批量预测与校准");
        Console.WriteLine("  wows-intel survey <目录>                     统计解析结果分布");
    }

    private sealed class BacktestRow
    {
        public string BattleId { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public double Predicted { get; set; }
        public double Actual { get; set; }
        public string Outcome { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public int AllyCount { get; set; }
    }
}
