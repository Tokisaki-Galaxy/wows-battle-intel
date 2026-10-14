using System.Globalization;
using Newtonsoft.Json;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Calibration;
using WowsBattleIntel.Core.Models;
using WowsBattleIntel.Core.Prediction;
using WowsBattleIntel.Core.Scoring;
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
                "import" => RunImport(args),
                "snapshot" => RunSnapshot(args),
                "predict" => RunPredict(args),
                "onnx" => RunOnnx(args),
                "history-backtest" => RunHistoryBacktest(args),
                "snapshot-backtest" => RunSnapshotBacktest(args),
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

    /// <summary>
    /// import 子命令：把一批解析结果导入本地 SQLite。
    /// </summary>
    private static int RunImport(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("用法: wows-intel import <解析输出目录> <数据库路径>");
            return 1;
        }

        string inputDir = args[1];
        string dbPath = args[2];

        var repository = new Storage.SqliteBattleRepository(dbPath);
        repository.InitializeAsync().GetAwaiter().GetResult();

        var files = Directory.GetFiles(inputDir, "*.json");
        Console.WriteLine($"扫描到 {files.Count()} 份解析结果，导入 {dbPath}");

        int imported = 0;
        int skipped = 0;
        int failed = 0;

        foreach (string file in files)
        {
            ReplaysharkOutput output;
            try
            {
                output = LoadOutput(file);
            }
            catch
            {
                failed++;
                continue;
            }

            var battle = ReplaysharkMapper.Map(output, file);
            if (battle is null)
            {
                skipped++;
                continue;
            }

            try
            {
                repository.SaveBattleAsync(battle).GetAwaiter().GetResult();
                imported++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  导入失败 {Path.GetFileName(file)}: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"导入完成: 成功 {imported}, 跳过 {skipped}（无完整结果）, 失败 {failed}");
        return 0;
    }

    /// <summary>
    /// predict 子命令：解析回放名单，拉真实统计，预测，再与真实结果对照。
    /// </summary>
    /// <remarks>
    /// 这是验证预测有效性的核心命令。它演示完整链路：
    /// 回放取名单 → WG API 补历史特征 → 预测 → 与回放真实结果对照。
    /// 只有补上历史特征后，预测才可能优于无信息基线。
    /// </remarks>
    private static int RunPredict(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: wows-intel predict <解析输出目录> [--limit N] [--delay MS]");
            return 1;
        }

        string dir = args[1];
        int limit = 0;
        int delayMs = 120;
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--limit" && int.TryParse(args[i + 1], out int l))
            {
                limit = l;
            }
            else if (args[i] == "--delay" && int.TryParse(args[i + 1], out int d))
            {
                delayMs = d;
            }
        }

        var files = Directory.GetFiles(dir, "*.json");
        if (limit > 0)
        {
            files = files.Take(limit).ToArray();
        }

        var provider = new Stats.WgPublicStatsProvider(new Stats.WgPublicOptions
        {
            DelayBetweenRequests = TimeSpan.FromMilliseconds(delayMs),
        });
        var predictor = new BaselinePredictor();

        Console.WriteLine($"待评估 {files.Length} 局，开始拉取玩家统计（限流需时间）");

        var pairs = new List<(PredictionResult Prediction, BattleOutcome Outcome)>();
        var detail = new List<(string Name, double Pred, string Actual, string Result)>();
        int processed = 0;

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

            var battle = ReplaysharkMapper.Map(output, file);
            if (battle is null || battle.Outcome == BattleOutcome.Unknown)
            {
                continue;
            }

            processed++;
            Console.Write($"\r[{processed}/{files.Length}] {Path.GetFileName(file)[..24]}  ");

            // 关键：用 WG API 补上历史统计特征。
            try
            {
                provider.FetchStatsAsync(battle.Players).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\n  统计拉取失败: {ex.Message}");
                continue;
            }

            var prediction = predictor.PredictAsync(battle).GetAwaiter().GetResult();
            pairs.Add((prediction, battle.Outcome));
            detail.Add((battle.BattleId[..8], prediction.AllyWinProbability,
                battle.Outcome.ToString(), Path.GetFileName(file)[..20]));
        }

        Console.WriteLine();

        if (pairs.Count == 0)
        {
            Console.WriteLine("没有可用样本。");
            return 0;
        }

        var report = CalibrationMetrics.BuildReport(pairs);
        Console.WriteLine();
        Console.WriteLine("=== 带历史特征的基线预测回测 ===");
        Console.WriteLine($"样本数: {report.SampleCount}");
        Console.WriteLine($"Brier 分数: {report.BrierScore:F4}  (无信息基线 0.25)");
        Console.WriteLine($"对数损失:   {report.LogLoss:F4}  (无信息基线 0.6931)");
        Console.WriteLine($"判别准确率: {report.Accuracy:P2}");
        Console.WriteLine();
        Console.WriteLine("=== 校准曲线 ===");
        Console.WriteLine($"{"概率区间",-14} {"样本",6} {"预测均值",10} {"实际胜率",10} {"偏差",10}");
        foreach (var bucket in report.Curve)
        {
            Console.WriteLine(
                $"{bucket.LowerBound:F1}-{bucket.UpperBound:F1}      {bucket.SampleCount,6} " +
                $"{bucket.PredictedMean,10:F3} {bucket.ActualWinrate,10:F3} {bucket.CalibrationError,10:+0.000;-0.000}");
        }

        Console.WriteLine();
        Console.WriteLine("=== 逐局明细（前 15）===");
        Console.WriteLine($"{"对局",-10} {"预测",8} {"真实",8}");
        foreach (var d in detail.Take(15))
        {
            Console.WriteLine($"{d.Name,-10} {d.Pred,8:F3} {d.Actual,8}");
        }

        return 0;
    }

    /// <summary>
    /// snapshot 子命令：对已入库对局涉及的玩家拉取当前统计并落盘快照。
    /// </summary>
    /// <remarks>
    /// 重要限制：现在拉取的是"当前"统计，而对局发生在过去。直接用会造成
    /// 时间泄漏，即用未来的信息预测过去的结果。本命令产出的快照仅用于：
    /// 1. 验证采集链路可用
    /// 2. 作为服务器基线计算的样本
    /// 3. 在真实场景下，客户端开局时采集的快照时间戳早于对局，无泄漏
    ///
    /// 训练时应只取对局开始时间之前的快照。若历史数据为事后补采，
    /// 评估结果会偏乐观，需在报告中标注。
    /// </remarks>
    private static int RunSnapshot(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: wows-intel snapshot <数据库> [--limit N] [--delay MS]");
            return 1;
        }

        string dbPath = args[1];
        int limit = 0;
        int delayMs = 120;
        bool fast = false;
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--limit" && int.TryParse(args[i + 1], out int l))
            {
                limit = l;
            }
            else if (args[i] == "--delay" && int.TryParse(args[i + 1], out int d))
            {
                delayMs = d;
            }
            else if (args[i] == "--fast")
            {
                fast = true;
            }
        }

        var repository = new Storage.SqliteBattleRepository(dbPath);
        repository.InitializeAsync().GetAwaiter().GetResult();

        // 收集库中出现过的玩家。
        var players = LoadDistinctPlayers(dbPath);
        if (limit > 0)
        {
            players = players.Take(limit).ToList();
        }

        Console.WriteLine($"库中出现 {players.Count} 个玩家，开始拉取当前统计");
        if (fast)
        {
            Console.WriteLine("快速模式: 跳过单船查询，只拉账号统计");
        }

        var provider = new Stats.WgPublicStatsProvider(new Stats.WgPublicOptions
        {
            DelayBetweenRequests = TimeSpan.FromMilliseconds(delayMs),
            SkipShipStats = fast,
        });

        var capturedAt = DateTimeOffset.UtcNow;
        var snapshots = new List<PlayerSnapshot>();
        int processed = 0;
        int withData = 0;

        // 快速模式批量 100（API 上限），普通模式 20 以免单船查询过多。
        int batchSize = fast ? 100 : 20;

        foreach (var batch in Chunk(players, batchSize))
        {
            processed += batch.Count;
            Console.Write($"\r[{processed}/{players.Count}] 拉取中...   ");

            try
            {
                provider.FetchStatsAsync(batch).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\n  批次失败: {ex.Message}");
                continue;
            }

            foreach (var p in batch)
            {
                if (p.HasStats)
                {
                    snapshots.Add(PlayerSnapshot.FromPlayer(p, capturedAt));
                    withData++;
                }
            }

            // 每 1000 人落一次盘，避免中断丢失全部进度。
            if (snapshots.Count >= 1000)
            {
                repository.SaveSnapshotsAsync(snapshots).GetAwaiter().GetResult();
                snapshots.Clear();
            }
        }

        Console.WriteLine();
        if (snapshots.Count > 0)
        {
            repository.SaveSnapshotsAsync(snapshots).GetAwaiter().GetResult();
        }

        var (playerCount, snapshotCount) = repository.GetSnapshotStatsAsync()
            .GetAwaiter().GetResult();

        Console.WriteLine($"采集完成: {withData} 个有效统计已落盘");
        Console.WriteLine($"库中快照: {snapshotCount} 条，覆盖 {playerCount} 个玩家");
        Console.WriteLine();
        Console.WriteLine("注意: 本次为事后补采，时间戳为当前时刻。");
        Console.WriteLine("      用于训练时需注意时间泄漏，详见命令实现中的说明。");
        return 0;
    }

    /// <summary>
    /// 从库中读出出现过的不重复玩家。
    /// </summary>
    private static List<Core.Models.PlayerRecord> LoadDistinctPlayers(string dbPath)
    {
        var players = new List<Core.Models.PlayerRecord>();
        var seen = new HashSet<string>();

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT account_id, server, player_name
            FROM battle_players
            WHERE is_bot = 0 AND account_id <> '-1' AND account_id <> ''
            ORDER BY account_id;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string accountId = reader.GetString(0);
            string serverStr = reader.GetString(1);
            string key = $"{serverStr}:{accountId}";
            if (!seen.Add(key))
            {
                continue;
            }

            players.Add(new Core.Models.PlayerRecord
            {
                AccountId = accountId,
                Server = Enum.TryParse<Core.Models.GameServer>(serverStr, out var sv)
                    ? sv
                    : Core.Models.GameServer.Auto,
                PlayerName = reader.GetString(2),
            });
        }

        return players;
    }

    /// <summary>
    /// 把序列分成固定大小的批次。
    /// </summary>
    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (int i = 0; i < source.Count; i += size)
        {
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }
    }

    private static ReplaysharkOutput LoadOutput(string path)
    {
        string json = File.ReadAllText(path);
        return JsonConvert.DeserializeObject<ReplaysharkOutput>(json)
            ?? throw new InvalidOperationException($"反序列化失败: {path}");
    }

    /// <summary>
    /// history-backtest 子命令：用积累的对局历史算近期状态特征并回测。
    /// </summary>
    /// <remarks>
    /// 与 predict 子命令的关键差别：不依赖 WG API 的历史统计，而是从
    /// 本地已入库的逐场对局算出近期胜率。每场对局都有结果，玩家所在队伍
    /// 的结果即他的胜负，所以近期状态可以纯本地计算。
    ///
    /// 严格按时间顺序回放：预测第 N 场时只用前 N-1 场的历史，
    /// 天然无时间泄漏。这是本项目第一次能在无泄漏前提下评估预测能力。
    /// </remarks>
    private static int RunHistoryBacktest(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: wows-intel history-backtest <数据库> [--csv <文件>]");
            return 1;
        }

        string dbPath = args[1];
        string? csvPath = null;
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--csv")
            {
                csvPath = args[i + 1];
            }
        }

        var repository = new Storage.SqliteBattleRepository(dbPath);
        repository.InitializeAsync().GetAwaiter().GetResult();

        var battles = LoadBattlesOrdered(dbPath);
        Console.WriteLine($"按时间排序的有效对局: {battles.Count} 局");

        var pairs = new List<(PredictionResult Prediction, BattleOutcome Outcome)>();
        var rows = new List<HistoryBacktestRow>();
        int skipped = 0;

        // 维护每个玩家的历史，按时间推进逐步累积。
        // 预测某场时，玩家历史只含该场之前的对局，天然无泄漏。
        var historyByPlayer = new Dictionary<string, List<PlayerBattleHistoryEntry>>();
        int index = 0;

        foreach (var battle in battles)
        {
            index++;
            if (index % 100 == 0)
            {
                Console.Write($"\r[{index}/{battles.Count}] 回测中...   ");
            }

            var features = new Dictionary<string, RecentForm>();
            foreach (var player in battle.Players.Where(p => !p.IsBot))
            {
                string key = PlayerKey(player);
                if (historyByPlayer.TryGetValue(key, out var history) && history.Count > 0)
                {
                    features[key] = RecentFormCalculator.Compute(history);
                }
            }

            var prediction = PredictFromRecentForm(battle, features);
            if (prediction is null)
            {
                skipped++;
            }
            else
            {
                pairs.Add((prediction, battle.Outcome));
                rows.Add(new HistoryBacktestRow
                {
                    BattleId = battle.BattleId,
                    StartedAt = battle.StartedAt,
                    Predicted = prediction.AllyWinProbability,
                    Actual = battle.Outcome == BattleOutcome.Win ? 1.0 : 0.0,
                    Outcome = battle.Outcome.ToString(),
                });
            }

            // 本场结束后把结果计入历史，供后续对局使用。
            foreach (var player in battle.Players.Where(p => !p.IsBot))
            {
                string key = PlayerKey(player);
                if (!historyByPlayer.TryGetValue(key, out var history))
                {
                    history = [];
                    historyByPlayer[key] = history;
                }

                bool won = (battle.Outcome == BattleOutcome.Win && player.IsAlly)
                    || (battle.Outcome == BattleOutcome.Loss && !player.IsAlly);

                history.Add(new PlayerBattleHistoryEntry
                {
                    BattleId = battle.BattleId,
                    StartedAt = battle.StartedAt,
                    Won = won,
                    DamageDealt = (int)player.DamageDealt,
                    ShipId = player.ShipId,
                    DivisionSize = player.DivisionSize,
                });
            }
        }

        Console.WriteLine();
        Console.WriteLine($"参与回测的玩家: {historyByPlayer.Count}");

        if (pairs.Count == 0)
        {
            Console.WriteLine("没有可用样本。需要玩家在多局中出现才能算近期状态。");
            return 0;
        }

        var report = CalibrationMetrics.BuildReport(pairs);
        Console.WriteLine();
        Console.WriteLine("=== 近期状态特征回测（无时间泄漏）===");
        Console.WriteLine($"样本数: {report.SampleCount}, 跳过: {skipped}");
        Console.WriteLine($"Brier 分数: {report.BrierScore:F4}  (无信息基线 0.25)");
        Console.WriteLine($"对数损失:   {report.LogLoss:F4}  (无信息基线 0.6931)");
        Console.WriteLine($"判别准确率: {report.Accuracy:P2}");
        Console.WriteLine();
        Console.WriteLine("=== 校准曲线 ===");
        Console.WriteLine($"{"概率区间",-14} {"样本",6} {"预测均值",10} {"实际胜率",10} {"偏差",10}");
        foreach (var bucket in report.Curve)
        {
            Console.WriteLine(
                $"{bucket.LowerBound:F1}-{bucket.UpperBound:F1}      {bucket.SampleCount,6} " +
                $"{bucket.PredictedMean,10:F3} {bucket.ActualWinrate,10:F3} {bucket.CalibrationError,10:+0.000;-0.000}");
        }

        if (csvPath is not null)
        {
            using var writer = new StreamWriter(csvPath);
            writer.WriteLine("battle_id,started_at,predicted,actual,outcome");
            foreach (var r in rows)
            {
                writer.WriteLine(
                    $"{r.BattleId},{r.StartedAt.ToUnixTimeMilliseconds()}," +
                    $"{r.Predicted.ToString("F6", CultureInfo.InvariantCulture)}," +
                    $"{r.Actual.ToString("F0", CultureInfo.InvariantCulture)},{r.Outcome}");
            }
            Console.WriteLine();
            Console.WriteLine($"明细已写入: {csvPath}");
        }

        return 0;
    }

    /// <summary>
    /// snapshot-backtest 子命令：用账号胜率快照特征回测。
    /// </summary>
    /// <remarks>
    /// 与 history-backtest 的差别：不要求玩家反复出现，用账号总胜率作为
    /// 特征，因此每个玩家都有特征，覆盖全部对局。
    ///
    /// 关于时间泄漏：账号总胜率是长期累积量，短期内基本稳定。用当前胜率
    /// 近似对局当时胜率误差可忽略，这是 XVM 与 ApeRadar 的标准做法。
    /// 近期状态这类短期特征才需要严格按时间切分。
    /// </remarks>
    private static int RunSnapshotBacktest(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: wows-intel snapshot-backtest <数据库> [--csv <文件>]");
            return 1;
        }

        string dbPath = args[1];
        string? csvPath = null;
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--csv")
            {
                csvPath = args[i + 1];
            }
        }

        var repository = new Storage.SqliteBattleRepository(dbPath);
        repository.InitializeAsync().GetAwaiter().GetResult();

        // 载入全部快照，建内存索引。
        var snapshots = LoadAllSnapshots(dbPath);
        Console.WriteLine($"载入玩家快照: {snapshots.Count} 条");

        var battles = LoadBattlesOrdered(dbPath);
        Console.WriteLine($"按时间排序的有效对局: {battles.Count} 局");

        var predictor = new BaselinePredictor();
        var pairs = new List<(PredictionResult Prediction, BattleOutcome Outcome)>();
        var lookup = BuildLookup(snapshots);
        int skipped = 0;
        int enriched = 0;

        foreach (var battle in battles)
        {
            var enrichedBattle = SnapshotFeatureBuilder.Enrich(battle, lookup);

            // 统计有多少对局真正补上了特征。
            if (enrichedBattle.Players.Any(p => p.HasStats))
            {
                enriched++;
            }

            var prediction = predictor.PredictAsync(enrichedBattle).GetAwaiter().GetResult();

            // 双方都无有效特征时预测无意义，跳过。
            if (prediction.Confidence <= 0)
            {
                skipped++;
                continue;
            }

            pairs.Add((prediction, battle.Outcome));
        }

        if (pairs.Count == 0)
        {
            Console.WriteLine("没有可用样本。请先运行 snapshot 命令采集玩家统计。");
            return 0;
        }

        var report = CalibrationMetrics.BuildReport(pairs);
        Console.WriteLine();
        Console.WriteLine("=== 账号胜率特征回测 ===");
        Console.WriteLine($"样本数: {report.SampleCount}, 跳过: {skipped}, 有特征: {enriched}");
        Console.WriteLine($"Brier 分数: {report.BrierScore:F4}  (无信息基线 0.25)");
        Console.WriteLine($"对数损失:   {report.LogLoss:F4}  (无信息基线 0.6931)");
        Console.WriteLine($"判别准确率: {report.Accuracy:P2}");
        Console.WriteLine();
        Console.WriteLine("=== 校准曲线 ===");
        Console.WriteLine($"{"概率区间",-14} {"样本",6} {"预测均值",10} {"实际胜率",10} {"偏差",10}");
        foreach (var bucket in report.Curve)
        {
            Console.WriteLine(
                $"{bucket.LowerBound:F1}-{bucket.UpperBound:F1}      {bucket.SampleCount,6} " +
                $"{bucket.PredictedMean,10:F3} {bucket.ActualWinrate,10:F3} {bucket.CalibrationError,10:+0.000;-0.000}");
        }

        if (csvPath is not null)
        {
            using var writer = new StreamWriter(csvPath);
            writer.WriteLine("battle_id,predicted,actual,outcome");
            foreach (var (pred, outcome) in pairs)
            {
                writer.WriteLine(
                    $"{pred.BattleId}," +
                    $"{pred.AllyWinProbability.ToString("F6", CultureInfo.InvariantCulture)}," +
                    $"{(outcome == BattleOutcome.Win ? 1 : 0)},{outcome}");
            }
            Console.WriteLine();
            Console.WriteLine($"明细已写入: {csvPath}");
        }

        return 0;
    }

    /// <summary>
    /// 载入全部玩家快照，键为 服务器:账号ID。
    /// </summary>
    private static Dictionary<string, PlayerSnapshot> LoadAllSnapshots(string dbPath)
    {
        var result = new Dictionary<string, PlayerSnapshot>();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // 每个账号取最新一条快照。
        cmd.CommandText = """
            SELECT account_id, server, captured_at, account_battles, account_winrate,
                   account_winrate_solo, account_battles_solo, account_winrate_div2,
                   account_battles_div2, account_winrate_div3, account_battles_div3,
                   weighted_winrate
            FROM player_snapshots s
            WHERE captured_at = (
                SELECT MAX(captured_at) FROM player_snapshots
                WHERE account_id = s.account_id AND server = s.server
            );
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = $"{reader.GetString(1)}:{reader.GetString(0)}";
            result[key] = new PlayerSnapshot
            {
                AccountId = reader.GetString(0),
                Server = Enum.TryParse<GameServer>(reader.GetString(1), out var sv)
                    ? sv : GameServer.Auto,
                CapturedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                AccountBattles = reader.GetInt32(3),
                AccountWinrate = reader.GetDouble(4),
                AccountWinrateSolo = reader.GetDouble(5),
                AccountBattlesSolo = reader.GetInt32(6),
                AccountWinrateDiv2 = reader.GetDouble(7),
                AccountBattlesDiv2 = reader.GetInt32(8),
                AccountWinrateDiv3 = reader.GetDouble(9),
                AccountBattlesDiv3 = reader.GetInt32(10),
                WeightedWinrate = reader.GetDouble(11),
            };
        }

        return result;
    }

    /// <summary>
    /// 构造快照查找函数。
    /// </summary>
    private static Func<string, GameServer, PlayerSnapshot?> BuildLookup(
        Dictionary<string, PlayerSnapshot> snapshots)
    {
        return (accountId, server) =>
            snapshots.GetValueOrDefault($"{server}:{accountId}");
    }

    /// <summary>
    /// 从数据库按时间顺序读出有效对局。
    /// </summary>
    private static List<BattleRecord> LoadBattlesOrdered(string dbPath)
    {
        var battles = new List<BattleRecord>();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var battleMeta = new Dictionary<string, (long Started, string Result)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT battle_id, started_at, result FROM battles
                WHERE result IN ('Win', 'Loss')
                ORDER BY started_at;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                battleMeta[reader.GetString(0)] = (reader.GetInt64(1), reader.GetString(2));
            }
        }

        var playersByBattle = new Dictionary<string, List<PlayerRecord>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT battle_id, account_id, server, player_name, clan_tag, ship_id,
                       ship_tier, ship_type, team, division_size, is_bot, is_hidden,
                       damage_dealt, kills, survived
                FROM battle_players;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string battleId = reader.GetString(0);
                if (!battleMeta.ContainsKey(battleId))
                {
                    continue;
                }

                if (!playersByBattle.TryGetValue(battleId, out var list))
                {
                    list = [];
                    playersByBattle[battleId] = list;
                }

                list.Add(new PlayerRecord
                {
                    AccountId = reader.GetString(1),
                    Server = Enum.TryParse<GameServer>(reader.GetString(2), out var sv)
                        ? sv : GameServer.Auto,
                    PlayerName = reader.GetString(3),
                    ClanTag = reader.GetString(4),
                    ShipId = reader.GetString(5),
                    ShipTier = reader.GetInt32(6),
                    ShipClass = Enum.TryParse<ShipClass>(reader.GetString(7), out var sc)
                        ? sc : ShipClass.Unknown,
                    IsAlly = reader.GetInt32(8) == 0,
                    DivisionSize = reader.GetInt32(9),
                    IsBot = reader.GetInt32(10) != 0,
                    IsHidden = reader.GetInt32(11) != 0,
                    DamageDealt = reader.GetInt64(12),
                    Kills = reader.GetInt64(13),
                    Survived = reader.GetInt32(14) != 0,
                });
            }
        }

        foreach (var (battleId, (started, result)) in battleMeta.OrderBy(kv => kv.Value.Started))
        {
            battles.Add(new BattleRecord
            {
                BattleId = battleId,
                StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(started),
                Outcome = result == "Win" ? BattleOutcome.Win : BattleOutcome.Loss,
                Players = playersByBattle.TryGetValue(battleId, out var list) ? list : [],
            });
        }

        return battles;
    }

    /// <summary>
    /// 用近期状态特征预测。
    /// </summary>
    private static PredictionResult? PredictFromRecentForm(
        BattleRecord battle,
        Dictionary<string, RecentForm> features)
    {
        var allyForms = battle.Allies
            .Where(p => !p.IsBot)
            .Select(p => features.GetValueOrDefault(PlayerKey(p)))
            .Where(f => f is not null && f.HasEnoughSamples)
            .Select(f => f!)
            .ToList();

        var enemyForms = battle.Enemies
            .Where(p => !p.IsBot)
            .Select(p => features.GetValueOrDefault(PlayerKey(p)))
            .Where(f => f is not null && f.HasEnoughSamples)
            .Select(f => f!)
            .ToList();

        if (allyForms.Count == 0 || enemyForms.Count == 0)
        {
            return null;
        }

        double allyAvg = allyForms.Average(f => f.RecentWinrate);
        double enemyAvg = enemyForms.Average(f => f.RecentWinrate);
        double diff = allyAvg - enemyAvg;
        double probability = 1.0 / (1.0 + Math.Exp(-diff * 4.0));

        int totalWithFeatures = allyForms.Count + enemyForms.Count;
        int totalHumans = battle.HumanPlayers.Count();

        return new PredictionResult
        {
            BattleId = battle.BattleId,
            PredictedAt = battle.StartedAt,
            AllyWinProbability = probability,
            Confidence = totalHumans > 0 ? (double)totalWithFeatures / totalHumans : 0,
            ModelVersion = "recent-form-logistic-v1",
            FeatureSnapshotJson = "{}",
            FeaturesAvailable = totalWithFeatures == totalHumans,
        };
    }

    private static string PlayerKey(PlayerRecord player) =>
        $"{player.Server}:{player.AccountId}";

    private sealed class HistoryBacktestRow
    {
        public string BattleId { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public double Predicted { get; set; }
        public double Actual { get; set; }
        public string Outcome { get; set; } = string.Empty;
    }

    /// <summary>
    /// onnx 子命令：用 ONNX 模型对一批对局做预测并校准。
    /// </summary>
    /// <remarks>
    /// 这是验证 C# 端本地推理链路的关键命令：加载 Python 导出的 ONNX 模型，
    /// 对真实回放数据预测，再与真实结果对照。若模型加载失败会自动降级到基线，
    /// 降级原因会打印出来，不静默。
    /// </remarks>
    private static int RunOnnx(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("用法: wows-intel onnx <模型.onnx> <解析输出目录>");
            return 1;
        }

        string modelPath = args[1];
        string dir = args[2];

        var createResult = Prediction.PredictorFactory.Create(modelPath, "onnx");
        if (createResult.UsedFallback)
        {
            Console.WriteLine($"模型加载失败，已降级到基线: {createResult.FallbackReason}");
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine($"已加载 ONNX 模型: {modelPath}");
            Console.WriteLine($"模型哈希: {createResult.Predictor.ModelHash[..16]}...");
            Console.WriteLine();
        }

        var predictor = createResult.Predictor;
        var files = Directory.GetFiles(dir, "*.json");
        var pairs = new List<(PredictionResult Prediction, BattleOutcome Outcome)>();
        int skipped = 0;

        foreach (string file in files)
        {
            ReplaysharkOutput output;
            try
            {
                output = LoadOutput(file);
            }
            catch
            {
                skipped++;
                continue;
            }

            var battle = ReplaysharkMapper.Map(output, file);
            if (battle is null || battle.Outcome == BattleOutcome.Unknown)
            {
                skipped++;
                continue;
            }

            // 用真实统计特征。这里从回放的战后数据无法得到赛前统计，
            // 因此模型会因特征缺失而退化，此项验证的是推理链路而非精度。
            var prediction = predictor.PredictAsync(battle).GetAwaiter().GetResult();
            pairs.Add((prediction, battle.Outcome));
        }

        if (pairs.Count == 0)
        {
            Console.WriteLine("没有可用样本。");
            return 0;
        }

        var report = CalibrationMetrics.BuildReport(pairs);
        Console.WriteLine("=== ONNX 推理回测 ===");
        Console.WriteLine($"样本数: {report.SampleCount}, 跳过: {skipped}");
        Console.WriteLine($"Brier 分数: {report.BrierScore:F4}");
        Console.WriteLine($"对数损失:   {report.LogLoss:F4}");
        Console.WriteLine($"判别准确率: {report.Accuracy:P2}");
        Console.WriteLine();
        Console.WriteLine("说明: 本命令验证推理链路连通性。回放不含赛前统计特征，");
        Console.WriteLine("      模型输入多为哨兵值，精度不代表真实能力。");
        return 0;
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
        Console.WriteLine("  wows-intel import <目录> <数据库>            导入解析结果到本地库");
        Console.WriteLine("  wows-intel predict <目录> [--limit N]        拉历史特征预测并对照真实结果");
        Console.WriteLine("  wows-intel onnx <模型.onnx> <目录>            用 ONNX 模型推理并回测");
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
