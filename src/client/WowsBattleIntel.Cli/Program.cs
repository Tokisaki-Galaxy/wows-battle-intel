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
                "import" => RunImport(args).GetAwaiter().GetResult(),
                "snapshot" => RunSnapshot(args).GetAwaiter().GetResult(),
                "predict" => RunPredict(args),
                "onnx" => RunOnnx(args),
                "history-backtest" => RunHistoryBacktest(args),
                "snapshot-backtest" => RunSnapshotBacktest(args),
                "dedupe-battles" => RunDedupeBattles(args),
                "rebuild-players" => RunRebuildPlayers(args),
                "backfill-predictions" => RunBackfillPredictions(args).GetAwaiter().GetResult(),
                "live" => RunLive(args).GetAwaiter().GetResult(),
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
    private static async Task<int> RunImport(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("用法: wows-intel import <解析输出目录> <数据库路径>");
            return 1;
        }

        string inputDir = args[1];
        string dbPath = args[2];

        var repository = new Storage.SqliteBattleRepository(dbPath);
        await repository.InitializeAsync().ConfigureAwait(false);

        var files = Directory.GetFiles(inputDir, "*.json");
        Console.WriteLine($"扫描到 {files.Count()} 份解析结果，导入 {dbPath}");

        // 早期版本的 import 只落对局不落预测，那批记录在列表里永远显示
        // 未预测。这里对齐客户端入库链路：落库后按同一套预测器补一条
        // 预测记录。同一局重复导入时落库幂等，预测也不会重复写
        // （按 battle_id 幂等判定，见 ShouldWritePredictionAsync）。
        var predictor = new BaselinePredictor();

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
                await repository.SaveBattleAsync(battle).ConfigureAwait(false);
                imported++;

                if (await ShouldWritePredictionAsync(repository, battle).ConfigureAwait(false))
                {
                    try
                    {
                        var prediction = await predictor.PredictAsync(battle).ConfigureAwait(false);
                        await repository.SavePredictionAsync(prediction).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 预测失败不影响对局落库，与客户端入库服务同一口径。
                        Console.Error.WriteLine($"  预测失败 {Path.GetFileName(file)}: {ex.Message}");
                    }
                }
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
    /// 判断一局是否还需要补写预测记录。
    /// </summary>
    /// <remarks>
    /// 已有预测的局跳过，重复导入不会堆出多条预测；非 PvP 对局不出预测，
    /// 与客户端入库服务同一白名单。
    /// </remarks>
    private static async Task<bool> ShouldWritePredictionAsync(
        Storage.SqliteBattleRepository repository,
        BattleRecord battle)
    {
        if (!battle.BattleType.IsPvpType())
        {
            return false;
        }

        return await repository.GetPredictionAsync(battle.BattleId) is null;
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
            // 回测只关心账号统计特征，不需要军团标签，省去额外请求。
            FetchClanTags = false,
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
    private static async Task<int> RunSnapshot(string[] args)
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

        // 断点续传：剔除近期已采过快照的玩家，中断后重跑不浪费请求。
        // 按"距上次采集不足 72 小时"判定，跨天零点重跑不再整批重采，
        // 只有真正陈旧（三天前）的快照才刷新。
        var since = DateTimeOffset.Now.AddHours(-72);
        var doneKeys = await repository.GetSnapshotAccountKeysSinceAsync(since)
            .ConfigureAwait(true);
        int skipped = players.Count(p => doneKeys.Contains($"{p.AccountId}:{p.Server}"));
        players.RemoveAll(p => doneKeys.Contains($"{p.AccountId}:{p.Server}"));

        Console.WriteLine($"库中出现 {players.Count + skipped} 个玩家，近期已采 {skipped} 个，本次待采 {players.Count} 个");
        if (players.Count == 0)
        {
            Console.WriteLine("全部玩家近期均已采集，无需重跑。");
            return 0;
        }

        Console.WriteLine("开始拉取当前统计");
        if (fast)
        {
            Console.WriteLine("快速模式: 跳过单船查询，只拉账号统计");
        }

        var provider = new Stats.WgPublicStatsProvider(new Stats.WgPublicOptions
        {
            DelayBetweenRequests = TimeSpan.FromMilliseconds(delayMs),
            SkipShipStats = fast,

            // 分舰种聚合与单船统计同 endpoint，一次请求两者都拿，默认开启。
            FetchClassStats = true,

            // 采集只落统计快照，军团标签不是特征，省去额外请求。
            FetchClanTags = false,
        });

        var capturedAt = DateTimeOffset.UtcNow;
        var snapshots = new List<PlayerSnapshot>();
        int processed = 0;
        int withData = 0;
        int failedPlayers = 0;

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
                // 批次失败记数但不中断，重跑会自动补这些玩家。
                failedPlayers += batch.Count;
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
        if (failedPlayers > 0)
        {
            Console.WriteLine($"批次失败 {failedPlayers} 个玩家未采集，重跑本命令将自动补采");
        }

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
            SELECT account_id, server, player_name, ship_id
            FROM battle_players
            WHERE is_bot = 0 AND account_id <> '-1' AND account_id <> ''
            ORDER BY account_id;
            """;

        // 同一账号玩多艘船时只保留第一次出现的船，保证补采的可重复性。
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
                ShipId = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
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
    /// 构造快照查找函数。离线回测只用账号层特征，shipId 参数忽略。
    /// </summary>
    private static Func<string, GameServer, string, PlayerSnapshot?> BuildLookup(
        Dictionary<string, PlayerSnapshot> snapshots)
    {
        return (accountId, server, _) =>
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
    /// dedupe-battles 子命令：清理历史重复对局与无效预测。
    /// </summary>
    /// <remarks>
    /// 两类清理对象：
    /// 1. 同局重复。早期入库没有同局收敛，同一局战斗被多个玩家的回放各录
    ///    一次，判定口径与在线链路一致：参战玩家集合相同、地图相同、开始
    ///    时间差小于 760 秒。每组保留 started_at 最早的一条。
    /// 2. 非 PvP 预测。训练端只认 Random 与 Ranked，历史入库时对 Coop 与
    ///    Unknown 类型照常出了预测，这些记录对校准只有噪声价值。
    ///
    /// 默认 dry-run，只报计数不动库；加 --apply 才真删。删除按外键级联
    /// （battle_players 有 ON DELETE CASCADE），predictions 无外键约束，
    /// 需手动删。
    /// </remarks>
    private static int RunDedupeBattles(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: wows-intel dedupe-battles <数据库> [--apply]");
            return 1;
        }

        string dbPath = args[1];
        bool apply = args.Contains("--apply");

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"数据库不存在: {dbPath}");
            return 1;
        }

        var battles = LoadAllBattlesWithPlayers(dbPath);
        Console.WriteLine($"库中对局总数: {battles.Count}");

        // 同局分组：按时间推进，已保留的对局作为收敛锚点。
        // 组内保留最早 started_at，其余标记删除。
        var ordered = battles.OrderBy(b => b.StartedAt.ToUnixTimeMilliseconds()).ToList();
        var kept = new List<BattleRecord>();
        var duplicateIds = new HashSet<string>();
        int groups = 0;

        foreach (var battle in ordered)
        {
            var anchor = kept.FirstOrDefault(k => BattleFingerprint.IsEquivalentTo(battle, k));
            if (anchor is null)
            {
                kept.Add(battle);
            }
            else
            {
                duplicateIds.Add(battle.BattleId);
                groups++;
            }
        }

        // 非 PvP 预测：对局本身保留，仅清掉它的预测记录。
        var nonPvpWithPrediction = battles
            .Where(b => !b.BattleType.IsPvpType() && !duplicateIds.Contains(b.BattleId))
            .Select(b => b.BattleId)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("=== 同局重复 ===");
        Console.WriteLine($"重复对局: {duplicateIds.Count} 局，涉及 {groups} 组");
        foreach (string id in duplicateIds.Take(20))
        {
            var dup = battles.First(b => b.BattleId == id);
            Console.WriteLine($"  删除 {id[..Math.Min(16, id.Length)]} {dup.StartedAt:u} {dup.MapId}");
        }
        if (duplicateIds.Count > 20)
        {
            Console.WriteLine($"  ... 其余 {duplicateIds.Count - 20} 局略");
        }

        Console.WriteLine();
        Console.WriteLine("=== 非 PvP 预测 ===");
        Console.WriteLine($"待清理预测: {nonPvpWithPrediction.Count} 条（对局保留）");

        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("dry-run 模式，未修改数据库。加 --apply 执行删除。");
            return 0;
        }

        int deletedBattles = 0;
        int deletedPredictions = 0;
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var transaction = conn.BeginTransaction();

            // battle_players 有级联删除，snapshots 表与对局无关联，无需处理。
            foreach (string battleId in duplicateIds)
            {
                using (var predCmd = conn.CreateCommand())
                {
                    predCmd.Transaction = transaction;
                    predCmd.CommandText = "DELETE FROM predictions WHERE battle_id = $id;";
                    predCmd.Parameters.AddWithValue("$id", battleId);
                    deletedPredictions += predCmd.ExecuteNonQuery();
                }

                using var battleCmd = conn.CreateCommand();
                battleCmd.Transaction = transaction;
                battleCmd.CommandText = "DELETE FROM battles WHERE battle_id = $id;";
                battleCmd.Parameters.AddWithValue("$id", battleId);
                deletedBattles += battleCmd.ExecuteNonQuery();
            }

            foreach (string battleId in nonPvpWithPrediction)
            {
                using var predCmd = conn.CreateCommand();
                predCmd.Transaction = transaction;
                predCmd.CommandText = "DELETE FROM predictions WHERE battle_id = $id;";
                predCmd.Parameters.AddWithValue("$id", battleId);
                deletedPredictions += predCmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Console.WriteLine();
        Console.WriteLine($"删除完成: 对局 {deletedBattles} 局，预测 {deletedPredictions} 条");
        return 0;
    }

    /// <summary>
    /// 载入库中全部对局与参战者，供同局分组判定。
    /// </summary>
    private static List<BattleRecord> LoadAllBattlesWithPlayers(string dbPath)
    {
        var battles = new List<BattleRecord>();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var playersByBattle = new Dictionary<string, List<PlayerRecord>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT battle_id, account_id, server FROM battle_players;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string battleId = reader.GetString(0);
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
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT battle_id, started_at, map_id, battle_type FROM battles;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string battleId = reader.GetString(0);
                battles.Add(new BattleRecord
                {
                    BattleId = battleId,
                    StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                    MapId = reader.GetString(2),
                    BattleType = Enum.TryParse<BattleType>(reader.GetString(3), out var bt)
                        ? bt : BattleType.Unknown,
                    Players = playersByBattle.TryGetValue(battleId, out var list)
                        ? list : [],
                });
            }
        }

        return battles;
    }

    /// <summary>
    /// backfill-predictions 子命令：为库里缺预测的对局补算预测记录。
    /// </summary>
    /// <remarks>
    /// 早期版本的 import 子命令只落对局不落预测，那批记录在列表里永远
    /// 显示未预测。本命令遍历全库，按当前预测器逐局补算。预测特征取
    /// 参战者的最新快照，因此先跑 snapshot 再跑本命令，补出的预测才
    /// 基于完整数据。
    /// </remarks>
    private static async Task<int> RunBackfillPredictions(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: wows-intel backfill-predictions <数据库路径>");
            return 1;
        }

        string dbPath = args[1];
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"数据库不存在: {dbPath}");
            return 1;
        }

        var repository = new Storage.SqliteBattleRepository(dbPath);
        await repository.InitializeAsync().ConfigureAwait(false);

        // 列表入口拿全量对局，逐局查预测缺失。
        var entries = await repository
            .GetBattleListAsync(int.MaxValue)
            .ConfigureAwait(false);

        // 幂等判定先批量做完：逐局查预测每局开一次连接，几万局太慢。
        // 这里一次取全量预测标识，内存比对。
        var predicted = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={dbPath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT battle_id FROM predictions;";
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                predicted.Add(reader.GetString(0));
            }
        }

        var predictor = new BaselinePredictor();

        int written = 0;
        int skippedExisting = 0;
        int skippedNonPvp = 0;
        foreach (var entry in entries.Where(e => !e.IsPrebattle))
        {
            if (!entry.BattleType.IsPvpType())
            {
                skippedNonPvp++;
                continue;
            }

            if (!predicted.Add(entry.BattleId))
            {
                skippedExisting++;
                continue;
            }

            var battle = await repository
                .GetBattleAsync(entry.BattleId)
                .ConfigureAwait(false);
            if (battle is null)
            {
                continue;
            }

            try
            {
                var prediction = await predictor.PredictAsync(battle).ConfigureAwait(false);
                await repository.SavePredictionAsync(prediction).ConfigureAwait(false);
                written++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  预测失败 {entry.BattleId[..16]}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"补算完成: 新写 {written} 条预测，已有跳过 {skippedExisting} 局，非 PvP 跳过 {skippedNonPvp} 局");
        return 0;
    }

    /// <summary>
    /// rebuild-players 子命令：用存量回放 JSON 重建参战者的船信息与身份字段。
    /// </summary>
    /// <remarks>
    /// 背景：存量库的 battle_players.ship_id 存的是 replayshark 的内部索引码
    /// （如 PJSD012），WG API 不认，单船统计查询全部落空。上一版已修映射器，
    /// 现在取数字 ship_id（如 3445536752），但存量行与存量快照不会自愈。
    ///
    /// 本命令遍历 battles 表，按 replay_path 重新解析回放 JSON，取 Mapper
    /// 的船信息与身份映射，按身份主键只更新这几列，对局表现列与对局本身不动。
    /// 回放文件缺失或解析失败的局打印警告跳过，不中断。
    /// </remarks>
    private static int RunRebuildPlayers(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "用法: wows-intel rebuild-players <数据库> [--limit N] [--parsed-dir 目录]");
            return 1;
        }

        string dbPath = args[1];
        int limit = 0;
        string? parsedDir = null;
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--limit" && int.TryParse(args[i + 1], out int l))
            {
                limit = l;
            }
            else if (args[i] == "--parsed-dir")
            {
                parsedDir = args[i + 1];
            }
        }

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"数据库不存在: {dbPath}");
            return 1;
        }

        var repository = new Storage.SqliteBattleRepository(dbPath);
        repository.InitializeAsync().GetAwaiter().GetResult();

        var battles = LoadBattlesWithReplayPath(dbPath, limit);
        Console.WriteLine($"待重建对局: {battles.Count} 局");

        int total = battles.Count;
        int rebuilt = 0;
        int skipped = 0;
        int index = 0;

        foreach (var (battleId, replayPath) in battles)
        {
            index++;
            string fileName = Path.GetFileName(replayPath);

            // replay_path 通常是相对仓库根的 data\parsed\... 前缀，
            // --parsed-dir 可覆盖目录部分，文件名保持不变。
            string fullPath = parsedDir is null
                ? replayPath
                : Path.Combine(parsedDir, fileName);

            if (!File.Exists(fullPath))
            {
                Console.Error.WriteLine($"[{index}/{total}] 跳过 {fileName}: 回放文件不存在 {fullPath}");
                skipped++;
                continue;
            }

            try
            {
                var output = LoadOutput(fullPath);
                var battle = ReplaysharkMapper.Map(output, replayPath);
                if (battle is null)
                {
                    Console.Error.WriteLine($"[{index}/{total}] 跳过 {fileName}: 不含完整战斗结果");
                    skipped++;
                    continue;
                }

                int updated = repository.UpdatePlayerShipInfoAsync(battleId, battle.Players)
                    .GetAwaiter().GetResult();
                Console.WriteLine($"[{index}/{total}] {fileName} 更新 {updated} 行");
                rebuilt++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{index}/{total}] 跳过 {fileName}: {ex.Message}");
                skipped++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"重建完成: 更新 {rebuilt} 局, 跳过 {skipped} 局");
        return 0;
    }

    /// <summary>
    /// 读对局标识与回放路径，可选截取前 limit 条。
    /// </summary>
    private static List<(string BattleId, string ReplayPath)> LoadBattlesWithReplayPath(
        string dbPath,
        int limit)
    {
        var battles = new List<(string, string)>();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT battle_id, replay_path FROM battles
            WHERE replay_path IS NOT NULL AND replay_path <> ''
            ORDER BY started_at
            """;

        // 注意分号归属：LIMIT 必须并入同一条语句，附加到已有分号之后会被
        // 当作第二条语句静默忽略。
        cmd.CommandText = limit > 0
            ? $"{cmd.CommandText} LIMIT $limit;"
            : $"{cmd.CommandText};";
        if (limit > 0)
        {
            cmd.Parameters.AddWithValue("$limit", limit);
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            battles.Add((reader.GetString(0), reader.GetString(1)));
        }

        return battles;
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

    /// <summary>
    /// live 子命令：观测当前对局的 tempArenaInfo，拉真实战绩并打印。
    /// </summary>
    /// <remarks>
    /// 无界面地跑一遍客户端的赛前链路，用于排查"实时名单看不到战绩"。
    /// 递归找最新的 tempArenaInfo.json，解析名单，从 clientrunner.log
    /// 探测服务器，按昵称解析账号并拉账号与单船统计，逐行打印结果。
    /// 与客户端同口径：-1 哨兵玩家也进补拉，拉到即落库缓存。
    ///
    /// 默认只跑一轮；加 --watch 每秒重扫，文件更新就重新打印，直到 Ctrl+C。
    /// </remarks>
    private static async Task<int> RunLive(string[] args)
    {
        string gamePath = string.Empty;
        string dbPath = string.Empty;
        int delayMs = 120;
        bool watch = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--game" when i + 1 < args.Length:
                    gamePath = args[++i];
                    break;
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--delay" when i + 1 < args.Length && int.TryParse(args[i + 1], out int d):
                    delayMs = d;
                    i++;
                    break;
                case "--watch":
                    watch = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            gamePath = DetectGamePath();
        }

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            Console.Error.WriteLine("无法定位游戏目录，请用 --game 指定。");
            return 1;
        }

        string replayRoot = Path.Combine(gamePath, "replays");
        Console.WriteLine($"游戏目录: {gamePath}");
        Console.WriteLine($"回放目录: {replayRoot}");

        var server = ResolveServer(gamePath);
        Console.WriteLine($"服务器: {server}（来自 clientrunner.log）");

        // 船目录元数据回填：与客户端 App 启动时同一注入。
        ArenaInfoParser.ShipMetaLookup = shipId =>
            Stats.ShipCatalog.TryGetMeta(shipId, out var meta) && meta is not null
                ? (meta.Type, meta.Tier)
                : null;

        Storage.SqliteBattleRepository? repository = null;
        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            repository = new Storage.SqliteBattleRepository(dbPath);
            await repository.InitializeAsync().ConfigureAwait(false);
            Console.WriteLine($"缓存库: {dbPath}");
        }

        var rawProvider = new Stats.WgPublicStatsProvider(new Stats.WgPublicOptions
        {
            DelayBetweenRequests = TimeSpan.FromMilliseconds(delayMs),
            FetchClassStats = true,
        });

        // live 是读取展示路径，与界面同口径：有本地库时套缓存装饰器，同一玩家
        // 在 7 天有效期内重复出现直接命中本地，不再请求 WG。
        IPlayerStatsProvider provider = repository is null
            ? rawProvider
            : new Stats.CachingPlayerStatsProvider(
                rawProvider,
                repository,
                new Stats.PlayerStatsCacheOptions
                {
                    Enabled = true,
                    Ttl = TimeSpan.FromDays(7),
                });

        DateTime lastSeen = DateTime.MinValue;
        while (true)
        {
            string? latest = FindLatestArenaInfo(replayRoot);
            if (latest is null)
            {
                Console.WriteLine("未找到 tempArenaInfo.json，等待对局加载界面...");
            }
            else
            {
                DateTime wrote = File.GetLastWriteTimeUtc(latest);
                if (wrote > lastSeen)
                {
                    lastSeen = wrote;
                    await PrintLiveAsync(latest, server, provider, repository).ConfigureAwait(false);
                }
            }

            if (!watch)
            {
                return 0;
            }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
    }

    /// <summary>解析一份 tempArenaInfo 并打印补拉后的名单。</summary>
    private static async Task PrintLiveAsync(
        string path,
        GameServer server,
        IPlayerStatsProvider provider,
        Storage.SqliteBattleRepository? repository)
    {
        if (!ArenaInfoParser.TryRead(path, out var arena) || arena is null)
        {
            Console.WriteLine($"解析失败（可能正在写入）: {path}");
            return;
        }

        var battle = ArenaInfoParser.ToBattleRecord(arena);
        if (battle.Server == GameServer.Auto)
        {
            battle.Server = server;
            foreach (var p in battle.Players)
            {
                p.Server = server;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== 捕获 {battle.Players.Count} 人 · {battle.BattleType} · {battle.Server} · {DateTime.Now:HH:mm:ss} ===");

        var targets = battle.Players
            .Where(p => !p.IsBot && !string.IsNullOrEmpty(p.PlayerName))
            .ToList();

        try
        {
            await provider.FetchStatsAsync(targets).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"补拉失败: {ex.Message}");
        }

        if (repository is not null)
        {
            var capturedAt = DateTimeOffset.UtcNow;
            var snapshots = targets
                .Where(p => p.HasStats && p.AccountId != "-1")
                .Select(p => PlayerSnapshot.FromPlayer(p, capturedAt))
                .ToList();
            if (snapshots.Count > 0)
            {
                await repository.SaveSnapshotsAsync(snapshots).ConfigureAwait(false);
                Console.WriteLine($"已落库 {snapshots.Count} 条快照");
            }
        }

        Console.WriteLine($"{"昵称",-28} {"舰种",-5} {"船",-16} {"账号ID",-12} {"场次",7} {"胜率",7} {"隐藏",4}");
        foreach (var p in battle.Players.OrderBy(p => p.IsAlly ? 0 : 1).ThenBy(p => p.PlayerName, StringComparer.Ordinal))
        {
            string ship = Stats.ShipCatalog.TryGetChineseName(p.ShipId, out string? cn) && cn is not null
                ? cn : p.ShipId;
            string battles = p.AccountBattles >= 0 ? ((int)p.AccountBattles).ToString() : "-";
            string winrate = p.AccountWinrate >= 0 ? p.AccountWinrate.ToString("P1") : "-";
            string side = p.IsAlly ? "友" : "敌";
            Console.WriteLine($"{p.PlayerName,-28} {ShipClassLabel(p.ShipClass),-5} {Truncate(ship, 16),-16} {p.AccountId,-12} {battles,7} {winrate,7} {(p.IsHidden ? "是" : "否"),4} {side}");
        }

        int humans = battle.HumanPlayers.Count();
        int covered = battle.HumanPlayers.Count(p => p.HasStats);
        Console.WriteLine($"覆盖 {covered}/{humans}，隐藏 {battle.HumanPlayers.Count(p => p.IsHidden)}");
    }

    private static string ShipClassLabel(ShipClass shipClass) => shipClass.ToDisplayCode();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    /// <summary>
    /// 递归找修改时间最新的 tempArenaInfo.json。修改时间超过窗口的视为崩溃
    /// 残留，忽略，避免 live 命令把两个月前的旧对局当成当前对局。
    /// </summary>
    private static string? FindLatestArenaInfo(string replayRoot)
    {
        if (!Directory.Exists(replayRoot))
        {
            return null;
        }

        DateTime staleBefore = DateTime.UtcNow.AddHours(-1);
        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string file in Directory.EnumerateFiles(replayRoot, "tempArenaInfo.json", SearchOption.AllDirectories))
        {
            DateTime wrote;
            try
            {
                wrote = File.GetLastWriteTimeUtc(file);
            }
            catch (IOException)
            {
                continue;
            }

            if (wrote <= staleBefore)
            {
                continue;
            }

            if (wrote > bestTime)
            {
                bestTime = wrote;
                best = file;
            }
        }

        return best;
    }

    /// <summary>
    /// 探测游戏目录。CLI 不引用 WPF 客户端项目，这里做一份最小实现：
    /// 常见安装位置加 Steam 库文件，命中判定为 replays 目录存在。
    /// </summary>
    private static string DetectGamePath()
    {
        var candidates = new List<string>
        {
            @"D:\SteamLibrary\steamapps\common\World of Warships",
            @"C:\SteamLibrary\steamapps\common\World of Warships",
            @"C:\Games\World_of_Warships",
            @"D:\Games\World_of_Warships",
        };

        foreach (string root in new[] { @"D:\Steam", @"C:\Program Files (x86)\Steam" })
        {
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
            {
                continue;
            }

            foreach (string line in File.ReadLines(vdf))
            {
                int pathIndex = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                if (pathIndex < 0)
                {
                    continue;
                }

                int start = line.IndexOf('"', pathIndex + 6);
                int end = start < 0 ? -1 : line.IndexOf('"', start + 1);
                if (start < 0 || end < 0)
                {
                    continue;
                }

                string lib = line[(start + 1)..end].Replace("\\\\", "\\");
                candidates.Add(Path.Combine(lib, "steamapps", "common", "World of Warships"));
            }
        }

        return candidates.FirstOrDefault(c => Directory.Exists(Path.Combine(c, "replays")))
            ?? string.Empty;
    }

    /// <summary>读 clientrunner.log 的 Selected realm 行推断服务器。</summary>
    private static GameServer ResolveServer(string gamePath)
    {
        string logPath = Path.Combine(gamePath, "profile", "clientrunner.log");
        if (!File.Exists(logPath))
        {
            return GameServer.Auto;
        }

        try
        {
            string text;
            using (var stream = new FileStream(
                logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                text = reader.ReadToEnd();
            }

            const string prefix = "Selected realm: ";
            int index = text.LastIndexOf(prefix, StringComparison.Ordinal);
            if (index < 0)
            {
                return GameServer.Auto;
            }

            index += prefix.Length;
            int end = text.IndexOf('\n', index);
            string realm = (end < 0 ? text[index..] : text[index..end]).Trim();
            return realm.ToLowerInvariant() switch
            {
                "ru" => GameServer.Ru,
                "eu" => GameServer.Eu,
                "na" => GameServer.Na,
                "asia" => GameServer.Asia,
                "cn" => GameServer.Cn,
                _ => GameServer.Auto,
            };
        }
        catch
        {
            return GameServer.Auto;
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
        Console.WriteLine("  wows-intel snapshot <数据库> [--fast]        拉取玩家统计并落盘快照");
        Console.WriteLine("  wows-intel predict <目录> [--limit N]        拉历史特征预测并对照真实结果");
        Console.WriteLine("  wows-intel history-backtest <数据库>         用近期状态特征回测, 无时间泄漏");
        Console.WriteLine("  wows-intel snapshot-backtest <数据库>        用账号胜率快照特征回测");
        Console.WriteLine("  wows-intel dedupe-battles <数据库> [--apply] 清理同局重复与非PvP预测, 默认dry-run");
        Console.WriteLine("  wows-intel backfill-predictions <数据库>     为缺预测的对局补算预测记录");
        Console.WriteLine("  wows-intel rebuild-players <数据库> [--limit N] [--parsed-dir 目录]");
        Console.WriteLine("                                               用存量回放JSON重建参战者船信息");
        Console.WriteLine("  wows-intel live [--game 目录] [--db 库] [--watch]");
        Console.WriteLine("                                               观测当前对局名单并拉真实战绩");
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
