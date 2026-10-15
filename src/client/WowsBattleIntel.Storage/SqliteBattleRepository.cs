using Microsoft.Data.Sqlite;
using WowsBattleIntel.Core.Abstractions;
using WowsBattleIntel.Core.Calibration;
using WowsBattleIntel.Core.Models;

namespace WowsBattleIntel.Storage;

/// <summary>
/// SQLite 实现的战斗仓储。
/// </summary>
/// <remarks>
/// 对应数据模型文档的表结构。所有写入幂等，靠唯一约束与 UPSERT 保证，
/// 重复导入同一局不会产生重复记录。
///
/// 连接采用每次操作打开短连接的方式，SQLite 对短连接友好，
/// 且避免多线程共享连接带来的复杂度。写操作串行化由 SQLite 自身保证。
/// </remarks>
public sealed class SqliteBattleRepository : IBattleRepository, IPlayerSnapshotStore, IPlayerHistoryProvider
{
    private readonly string connectionString;

    /// <summary>
    /// 构造仓储。
    /// </summary>
    /// <param name="databasePath">SQLite 文件路径。传 ":memory:" 用于测试。</param>
    public SqliteBattleRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = databasePath == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = databasePath == ":memory:" ? SqliteCacheMode.Shared : SqliteCacheMode.Default,
        }.ToString();
    }

    /// <summary>
    /// 初始化数据库结构。幂等，可重复调用。
    /// </summary>
    /// <remarks>
    /// schema.sql 只覆盖建表，存量库的加列靠迁移补齐。SQLite 的
    /// ALTER TABLE ADD COLUMN 无 IF NOT EXISTS，先查表结构再决定。
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string schema = await LoadSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await MigrateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 存量库的结构迁移。逐版本加列，失败容忍重复执行。
    /// </summary>
    private static async Task MigrateSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(player_snapshots);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existing.Add(reader.GetString(1));
            }
        }

        if (!existing.Contains("class_stats"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE player_snapshots ADD COLUMN class_stats TEXT;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!existing.Contains("account_avg_damage"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE player_snapshots ADD COLUMN account_avg_damage REAL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 单船统计三列。快照默认只存账号层，带 ship_id 时才填这组值。
        if (!existing.Contains("ship_id"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE player_snapshots ADD COLUMN ship_id TEXT;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!existing.Contains("ship_battles"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE player_snapshots ADD COLUMN ship_battles INTEGER;
                ALTER TABLE player_snapshots ADD COLUMN ship_winrate REAL;
                ALTER TABLE player_snapshots ADD COLUMN ship_avg_damage REAL;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task SaveBattleAsync(
        BattleRecord battle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(battle);
        if (string.IsNullOrWhiteSpace(battle.BattleId))
        {
            throw new ArgumentException("战斗记录必须有对局标识。", nameof(battle));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await UpsertBattleAsync(connection, (SqliteTransaction)transaction, battle, cancellationToken)
            .ConfigureAwait(false);
        await UpsertPlayersAsync(connection, (SqliteTransaction)transaction, battle, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertBattleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BattleRecord battle,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO battles (
                battle_id, started_at, finished_at, map_id, battle_type, game_version,
                server, result, ally_score, enemy_score, duration_sec,
                replay_path, replay_parsed, created_at
            ) VALUES (
                $id, $started, $finished, $map, $type, $version,
                $server, $result, $allyScore, $enemyScore, $duration,
                $replay, $parsed, $created
            )
            ON CONFLICT(battle_id) DO UPDATE SET
                finished_at   = excluded.finished_at,
                map_id        = excluded.map_id,
                battle_type   = excluded.battle_type,
                game_version  = excluded.game_version,
                server        = excluded.server,
                result        = excluded.result,
                ally_score    = excluded.ally_score,
                enemy_score   = excluded.enemy_score,
                duration_sec  = excluded.duration_sec,
                replay_path   = excluded.replay_path,
                replay_parsed = excluded.replay_parsed;
            """;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        command.Parameters.AddWithValue("$id", battle.BattleId);
        command.Parameters.AddWithValue("$started", battle.StartedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$finished",
            battle.FinishedAt is { } f ? f.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("$map", battle.MapId ?? string.Empty);
        command.Parameters.AddWithValue("$type", battle.BattleType.ToStorageString());
        command.Parameters.AddWithValue("$version", battle.GameVersion ?? string.Empty);
        command.Parameters.AddWithValue("$server", battle.Server.ToString());
        command.Parameters.AddWithValue("$result", battle.Outcome.ToString());
        command.Parameters.AddWithValue("$allyScore", battle.AllyScore);
        command.Parameters.AddWithValue("$enemyScore", battle.EnemyScore);
        command.Parameters.AddWithValue("$duration", battle.DurationSeconds);
        command.Parameters.AddWithValue("$replay", (object?)battle.ReplayPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$parsed", battle.ReplayPath is not null ? 1 : 0);
        command.Parameters.AddWithValue("$created", now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertPlayersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BattleRecord battle,
        CancellationToken cancellationToken)
    {
        // 先删后插，保证参战者集合与当前解析结果一致。
        // 单局人数有限，代价可忽略。
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM battle_players WHERE battle_id = $id;";
            delete.Parameters.AddWithValue("$id", battle.BattleId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var p in battle.Players)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO battle_players (
                    battle_id, account_id, server, player_name, clan_tag,
                    ship_id, ship_name, ship_tier, ship_type, team, division_size,
                    is_bot, is_hidden, damage_dealt, damage_taken, kills,
                    spotting_damage, potential_damage, survived, survived_sec,
                    exp_earned, raw_json
                ) VALUES (
                    $battleId, $accountId, $server, $name, $clan,
                    $shipId, $shipName, $tier, $shipType, $team, $div,
                    $bot, $hidden, $dmgDealt, $dmgTaken, $kills,
                    $spot, $potential, $survived, $survivedSec,
                    $exp, NULL
                );
                """;

            command.Parameters.AddWithValue("$battleId", battle.BattleId);
            command.Parameters.AddWithValue("$accountId", p.AccountId);
            command.Parameters.AddWithValue("$server", p.Server.ToString());
            command.Parameters.AddWithValue("$name", p.PlayerName ?? string.Empty);
            command.Parameters.AddWithValue("$clan", p.ClanTag ?? string.Empty);
            command.Parameters.AddWithValue("$shipId", p.ShipId ?? string.Empty);
            command.Parameters.AddWithValue("$shipName", p.ShipName ?? string.Empty);
            command.Parameters.AddWithValue("$tier", p.ShipTier);
            command.Parameters.AddWithValue("$shipType", p.ShipClass.ToStorageString());
            command.Parameters.AddWithValue("$team", p.IsAlly ? 0 : 1);
            command.Parameters.AddWithValue("$div", p.DivisionSize);
            command.Parameters.AddWithValue("$bot", p.IsBot ? 1 : 0);
            command.Parameters.AddWithValue("$hidden", p.IsHidden ? 1 : 0);
            command.Parameters.AddWithValue("$dmgDealt", p.DamageDealt);
            command.Parameters.AddWithValue("$dmgTaken", p.DamageTaken);
            command.Parameters.AddWithValue("$kills", p.Kills);
            command.Parameters.AddWithValue("$spot", p.SpottingDamage);
            command.Parameters.AddWithValue("$potential", p.PotentialDamage);
            command.Parameters.AddWithValue("$survived", p.Survived ? 1 : 0);
            command.Parameters.AddWithValue("$survivedSec", p.SurvivedSeconds);
            command.Parameters.AddWithValue("$exp", p.ExpEarned);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task SavePredictionAsync(
        PredictionResult prediction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prediction);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO predictions (
                battle_id, predicted_at, model_version, model_hash,
                ally_win_prob, confidence, feature_json, features_available,
                actual_result, calibrated, created_at
            ) VALUES (
                $battleId, $predictedAt, $version, $hash,
                $prob, $confidence, $feature, $available,
                'Unknown', 0, $created
            );
            """;

        command.Parameters.AddWithValue("$battleId", prediction.BattleId);
        command.Parameters.AddWithValue("$predictedAt", prediction.PredictedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$version", prediction.ModelVersion ?? string.Empty);
        command.Parameters.AddWithValue("$hash", prediction.ModelHash ?? string.Empty);
        command.Parameters.AddWithValue("$prob", prediction.AllyWinProbability);
        command.Parameters.AddWithValue("$confidence", prediction.Confidence);
        command.Parameters.AddWithValue("$feature", (object?)prediction.FeatureSnapshotJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$available", prediction.FeaturesAvailable ? 1 : 0);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BattleRecord?> GetBattleAsync(
        string battleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(battleId);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        BattleRecord? battle = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM battles WHERE battle_id = $id;";
            command.Parameters.AddWithValue("$id", battleId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                battle = ReadBattle(reader);
            }
        }

        if (battle is null)
        {
            return null;
        }

        await using (var playersCmd = connection.CreateCommand())
        {
            playersCmd.CommandText = "SELECT * FROM battle_players WHERE battle_id = $id;";
            playersCmd.Parameters.AddWithValue("$id", battleId);

            await using var reader = await playersCmd.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                battle.Players.Add(ReadPlayer(reader));
            }
        }

        return battle;
    }

    /// <inheritdoc />
    public async Task BackfillOutcomeAsync(
        string battleId,
        BattleOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(battleId);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var battleCmd = connection.CreateCommand())
        {
            battleCmd.CommandText = "UPDATE battles SET result = $result WHERE battle_id = $id;";
            battleCmd.Parameters.AddWithValue("$result", outcome.ToString());
            battleCmd.Parameters.AddWithValue("$id", battleId);
            await battleCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 回填预测记录的真实结果，供校准消费。
        await using var predCmd = connection.CreateCommand();
        predCmd.CommandText = """
            UPDATE predictions
            SET actual_result = $result, calibrated = 0
            WHERE battle_id = $id AND actual_result = 'Unknown';
            """;
        predCmd.Parameters.AddWithValue("$result", outcome.ToString());
        predCmd.Parameters.AddWithValue("$id", battleId);
        await predCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PredictionResult>> GetUncalibratedPredictionsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT battle_id, predicted_at, model_version, model_hash,
                   ally_win_prob, confidence, feature_json, features_available
            FROM predictions
            WHERE calibrated = 0 AND actual_result <> 'Unknown'
            ORDER BY predicted_at
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<PredictionResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new PredictionResult
            {
                BattleId = reader.GetString(0),
                PredictedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                ModelVersion = reader.GetString(2),
                ModelHash = reader.GetString(3),
                AllyWinProbability = reader.GetDouble(4),
                Confidence = reader.GetDouble(5),
                FeatureSnapshotJson = reader.IsDBNull(6) ? "{}" : reader.GetString(6),
                FeaturesAvailable = reader.GetInt32(7) != 0,
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalibrationSample>> GetCalibrationSamplesAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT battle_id, predicted_at, model_version, model_hash,
                   ally_win_prob, confidence, feature_json, features_available,
                   actual_result
            FROM predictions
            WHERE actual_result IN ('Win', 'Loss')
            ORDER BY predicted_at
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var samples = new List<CalibrationSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // 查询已限定 Win/Loss，解析失败说明数据被外部改坏，跳过而不是抛。
            if (!Enum.TryParse<BattleOutcome>(reader.GetString(8), out var outcome)
                || outcome == BattleOutcome.Unknown)
            {
                continue;
            }

            samples.Add(new CalibrationSample(
                new PredictionResult
                {
                    BattleId = reader.GetString(0),
                    PredictedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                    ModelVersion = reader.GetString(2),
                    ModelHash = reader.GetString(3),
                    AllyWinProbability = reader.GetDouble(4),
                    Confidence = reader.GetDouble(5),
                    FeatureSnapshotJson = reader.IsDBNull(6) ? "{}" : reader.GetString(6),
                    FeaturesAvailable = reader.GetInt32(7) != 0,
                },
                outcome));
        }

        return samples;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BattleRecord>> GetRecentBattlesAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM battles
            ORDER BY started_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var battles = new List<BattleRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                battles.Add(ReadBattle(reader));
            }
        }

        return battles;
    }

    /// <inheritdoc />
    public async Task<int> CountBattlesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM battles;";

        object? result = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return result is long count ? (int)count : 0;
    }

    /// <summary>
    /// 为一批对局补齐参战者。列表查询为了性能不带名单，这里一次性填充。
    /// </summary>
    public async Task AttachPlayersAsync(
        IReadOnlyList<BattleRecord> battles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(battles);
        if (battles.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var battle in battles)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM battle_players WHERE battle_id = $id;";
            command.Parameters.AddWithValue("$id", battle.BattleId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                battle.Players.Add(ReadPlayer(reader));
            }
        }
    }

    /// <summary>
    /// 查账号在指定服务器上的最新快照，不设时间上界，供展示层使用。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="GetLatestSnapshotBeforeAsync"/> 的区别：预测特征必须
    /// 严格用对局之前的快照防时间泄漏，展示层给玩家看当前战绩，取最新即可。
    /// </remarks>
    public async Task<PlayerSnapshot?> GetLatestSnapshotAsync(
        string accountId,
        GameServer server,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM player_snapshots
            WHERE account_id = $accountId AND server = $server
            ORDER BY captured_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$server", server.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadSnapshot(reader);
    }

    /// <inheritdoc />
    public async Task<PredictionResult?> GetPredictionAsync(
        string battleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(battleId);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 同一局可能有多次预测，取最新一条，与界面展示的语义一致。
        command.CommandText = """
            SELECT battle_id, predicted_at, model_version, model_hash,
                   ally_win_prob, confidence, feature_json, features_available
            FROM predictions
            WHERE battle_id = $id
            ORDER BY predicted_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", battleId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PredictionResult
        {
            BattleId = reader.GetString(0),
            PredictedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            ModelVersion = reader.GetString(2),
            ModelHash = reader.GetString(3),
            AllyWinProbability = reader.GetDouble(4),
            Confidence = reader.GetDouble(5),
            FeatureSnapshotJson = reader.IsDBNull(6) ? "{}" : reader.GetString(6),
            FeaturesAvailable = reader.GetInt32(7) != 0,
        };
    }

    /// <inheritdoc />
    public async Task SaveSnapshotAsync(
        PlayerSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await SaveSnapshotsAsync([snapshot], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveSnapshotsAsync(
        IReadOnlyList<PlayerSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var s in snapshots)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            // 相同账号同一时间戳只保留一条。带 ship_id 时写入单船统计，
            // 账号层与单船层共存一张表，读侧按需取用。
            command.CommandText = """
                INSERT INTO player_snapshots (
                    account_id, server, captured_at,
                    account_battles, account_winrate, account_avg_damage,
                    account_winrate_solo, account_battles_solo,
                    account_winrate_div2, account_battles_div2,
                    account_winrate_div3, account_battles_div3,
                    weighted_winrate, pr, source, class_stats,
                    ship_id, ship_battles, ship_winrate, ship_avg_damage
                ) VALUES (
                    $accountId, $server, $capturedAt,
                    $battles, $winrate, $avgDamage,
                    $wrSolo, $bSolo,
                    $wrDiv2, $bDiv2,
                    $wrDiv3, $bDiv3,
                    $weighted, NULL, $source, $classStats,
                    $shipId, $shipBattles, $shipWinrate, $shipAvgDamage
                )
                ON CONFLICT(account_id, server, captured_at) DO NOTHING;
                """;

            command.Parameters.AddWithValue("$accountId", s.AccountId);
            command.Parameters.AddWithValue("$server", s.Server.ToString());
            command.Parameters.AddWithValue("$capturedAt", s.CapturedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$battles", s.AccountBattles);
            command.Parameters.AddWithValue("$winrate", s.AccountWinrate);
            command.Parameters.AddWithValue("$avgDamage", s.AccountAvgDamage);
            command.Parameters.AddWithValue("$wrSolo", s.AccountWinrateSolo);
            command.Parameters.AddWithValue("$bSolo", s.AccountBattlesSolo);
            command.Parameters.AddWithValue("$wrDiv2", s.AccountWinrateDiv2);
            command.Parameters.AddWithValue("$bDiv2", s.AccountBattlesDiv2);
            command.Parameters.AddWithValue("$wrDiv3", s.AccountWinrateDiv3);
            command.Parameters.AddWithValue("$bDiv3", s.AccountBattlesDiv3);
            command.Parameters.AddWithValue("$weighted", s.WeightedWinrate);
            command.Parameters.AddWithValue("$source", s.Source ?? string.Empty);
            var classStats = command.Parameters.Add("$classStats", SqliteType.Text);
            classStats.Value = string.IsNullOrEmpty(s.ClassStatsJson)
                ? DBNull.Value
                : s.ClassStatsJson;
            var shipId = command.Parameters.Add("$shipId", SqliteType.Text);
            shipId.Value = string.IsNullOrEmpty(s.ShipId) ? DBNull.Value : s.ShipId;
            command.Parameters.AddWithValue("$shipBattles", s.ShipBattles);
            command.Parameters.AddWithValue("$shipWinrate", s.ShipWinrate);
            command.Parameters.AddWithValue("$shipAvgDamage", s.ShipAvgDamage);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PlayerSnapshot?> GetLatestSnapshotBeforeAsync(
        string accountId,
        GameServer server,
        DateTimeOffset before,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM player_snapshots
            WHERE account_id = $accountId AND server = $server AND captured_at <= $before
            ORDER BY captured_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$server", server.ToString());
        command.Parameters.AddWithValue("$before", before.ToUnixTimeMilliseconds());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadSnapshot(reader);
    }

    /// <summary>从读取器当前位置构造快照对象。</summary>
    private static PlayerSnapshot ReadSnapshot(SqliteDataReader reader)
    {
        int shipIdOrdinal = TryGetOrdinal(reader, "ship_id");
        return new PlayerSnapshot
        {
            AccountId = reader.GetString(reader.GetOrdinal("account_id")),
            Server = Enum.TryParse<GameServer>(
                reader.GetString(reader.GetOrdinal("server")), out var sv) ? sv : GameServer.Auto,
            CapturedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                reader.GetInt64(reader.GetOrdinal("captured_at"))),
            AccountBattles = reader.GetInt32(reader.GetOrdinal("account_battles")),
            AccountWinrate = reader.GetDouble(reader.GetOrdinal("account_winrate")),
            // 旧快照无此列，迁移加列后为 NULL，落回 -1 哨兵。
            AccountAvgDamage = reader.IsDBNull(reader.GetOrdinal("account_avg_damage"))
                ? -1
                : reader.GetDouble(reader.GetOrdinal("account_avg_damage")),
            AccountWinrateSolo = reader.GetDouble(reader.GetOrdinal("account_winrate_solo")),
            AccountBattlesSolo = reader.GetInt32(reader.GetOrdinal("account_battles_solo")),
            AccountWinrateDiv2 = reader.GetDouble(reader.GetOrdinal("account_winrate_div2")),
            AccountBattlesDiv2 = reader.GetInt32(reader.GetOrdinal("account_battles_div2")),
            AccountWinrateDiv3 = reader.GetDouble(reader.GetOrdinal("account_winrate_div3")),
            AccountBattlesDiv3 = reader.GetInt32(reader.GetOrdinal("account_battles_div3")),
            WeightedWinrate = reader.GetDouble(reader.GetOrdinal("weighted_winrate")),
            ClassStatsJson = reader.IsDBNull(reader.GetOrdinal("class_stats"))
                ? null
                : reader.GetString(reader.GetOrdinal("class_stats")),

            // 单船三列，旧快照与账号级快照均无值，落回 -1 哨兵。
            ShipId = shipIdOrdinal >= 0 && !reader.IsDBNull(shipIdOrdinal)
                ? reader.GetString(shipIdOrdinal)
                : string.Empty,
            ShipBattles = TryGetDouble(reader, "ship_battles"),
            ShipWinrate = TryGetDouble(reader, "ship_winrate"),
            ShipAvgDamage = TryGetDouble(reader, "ship_avg_damage"),
        };
    }

    /// <summary>按列名取序号，列不存在时返回 -1，兼容旧库结构。</summary>
    private static int TryGetOrdinal(SqliteDataReader reader, string name)
    {
        try
        {
            return reader.GetOrdinal(name);
        }
        catch (ArgumentException)
        {
            return -1;
        }
    }

    /// <summary>按列名取可空实数，列不存在或为 NULL 时返回 -1 哨兵。</summary>
    private static double TryGetDouble(SqliteDataReader reader, string name)
    {
        int ordinal = TryGetOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal) ? -1 : reader.GetDouble(ordinal);
    }

    /// <inheritdoc />
    public async Task<(int PlayerCount, int SnapshotCount)> GetSnapshotStatsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(DISTINCT account_id || ':' || server) AS players,
                   COUNT(*) AS snapshots
            FROM player_snapshots;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (reader.GetInt32(0), reader.GetInt32(1));
        }

        return (0, 0);
    }

    /// <inheritdoc />
    public async Task<BattleRecord?> FindEquivalentBattleAsync(
        BattleRecord battle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(battle);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // 时间窗为开区间端点的候选：started_at 落在 (本局 - 窗口, 本局 + 窗口)。
        // started_at 有索引，窗口两端的记录由 IsEquivalentTo 精确复核。
        long windowMs = BattleFingerprint.EquivalentWindowSeconds * 1000L;
        long startedMs = battle.StartedAt.ToUnixTimeMilliseconds();

        var candidates = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT battle_id FROM battles
                WHERE map_id = $map
                  AND started_at > $windowStart
                  AND started_at < $windowEnd
                ORDER BY started_at;
                """;
            command.Parameters.AddWithValue("$map", battle.MapId ?? string.Empty);
            command.Parameters.AddWithValue("$windowStart", startedMs - windowMs);
            command.Parameters.AddWithValue("$windowEnd", startedMs + windowMs);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(reader.GetString(0));
            }
        }

        // SQL 只能筛时间与地图，参战者集合一致性在 C# 侧精查。
        // 命中一个即返回，按 started_at 升序取最早的那局作为收敛目标。
        foreach (string battleId in candidates)
        {
            var existing = await GetBattleAsync(battleId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null && BattleFingerprint.IsEquivalentTo(battle, existing))
            {
                return existing;
            }
        }

        return null;
    }

    private static BattleRecord ReadBattle(SqliteDataReader reader)
    {
        return new BattleRecord
        {
            BattleId = reader.GetString(reader.GetOrdinal("battle_id")),
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                reader.GetInt64(reader.GetOrdinal("started_at"))),
            MapId = reader.GetString(reader.GetOrdinal("map_id")),
            BattleType = Enum.TryParse<BattleType>(
                reader.GetString(reader.GetOrdinal("battle_type")), out var bt) ? bt : BattleType.Unknown,
            GameVersion = reader.GetString(reader.GetOrdinal("game_version")),
            Server = Enum.TryParse<GameServer>(
                reader.GetString(reader.GetOrdinal("server")), out var sv) ? sv : GameServer.Auto,
            Outcome = Enum.TryParse<BattleOutcome>(
                reader.GetString(reader.GetOrdinal("result")), out var oc) ? oc : BattleOutcome.Unknown,
            DurationSeconds = reader.GetInt32(reader.GetOrdinal("duration_sec")),
            Players = [],
        };
    }

    private static PlayerRecord ReadPlayer(SqliteDataReader reader)
    {
        return new PlayerRecord
        {
            AccountId = reader.GetString(reader.GetOrdinal("account_id")),
            Server = Enum.TryParse<GameServer>(
                reader.GetString(reader.GetOrdinal("server")), out var sv) ? sv : GameServer.Auto,
            PlayerName = reader.GetString(reader.GetOrdinal("player_name")),
            ClanTag = reader.GetString(reader.GetOrdinal("clan_tag")),
            ShipId = reader.GetString(reader.GetOrdinal("ship_id")),
            ShipName = reader.GetString(reader.GetOrdinal("ship_name")),
            ShipTier = reader.GetInt32(reader.GetOrdinal("ship_tier")),
            ShipClass = Enum.TryParse<ShipClass>(
                reader.GetString(reader.GetOrdinal("ship_type")), out var sc) ? sc : ShipClass.Unknown,
            IsAlly = reader.GetInt32(reader.GetOrdinal("team")) == 0,
            DivisionSize = reader.GetInt32(reader.GetOrdinal("division_size")),
            IsBot = reader.GetInt32(reader.GetOrdinal("is_bot")) != 0,
            IsHidden = reader.GetInt32(reader.GetOrdinal("is_hidden")) != 0,
            DamageDealt = reader.GetInt64(reader.GetOrdinal("damage_dealt")),
            DamageTaken = reader.GetInt64(reader.GetOrdinal("damage_taken")),
            Kills = reader.GetInt64(reader.GetOrdinal("kills")),
            SpottingDamage = reader.GetInt64(reader.GetOrdinal("spotting_damage")),
            PotentialDamage = reader.GetInt64(reader.GetOrdinal("potential_damage")),
            Survived = reader.GetInt32(reader.GetOrdinal("survived")) != 0,
            SurvivedSeconds = reader.GetInt32(reader.GetOrdinal("survived_sec")),
            ExpEarned = reader.GetInt64(reader.GetOrdinal("exp_earned")),
        };
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 关键查询：关联 battles 与 battle_players，取该玩家在指定时间点之前的
    /// 逐场记录。胜负由他所在队伍与对局结果共同决定：team 为 0 时对局获胜
    /// 即该玩家获胜，team 为 1 时相反。
    ///
    /// 索引 (account_id, battle_id) 与 battles.started_at 支撑此查询。
    /// </remarks>
    public async Task<IReadOnlyList<PlayerBattleHistoryEntry>> GetRecentBattlesAsync(
        string accountId,
        GameServer server,
        DateTimeOffset before,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.battle_id, b.started_at, b.result, p.team,
                   p.damage_dealt, p.ship_id, p.division_size
            FROM battle_players p
            INNER JOIN battles b ON b.battle_id = p.battle_id
            WHERE p.account_id = $accountId
              AND p.server = $server
              AND b.started_at < $before
              AND b.result IN ('Win', 'Loss')
            ORDER BY b.started_at DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$server", server.ToString());
        command.Parameters.AddWithValue("$before", before.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<PlayerBattleHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string battleResult = reader.GetString(2);
            int team = reader.GetInt32(3);

            // team 0 是对局结果持有方，team 1 相反。
            bool won = (battleResult == "Win" && team == 0)
                || (battleResult == "Loss" && team == 1);

            entries.Add(new PlayerBattleHistoryEntry
            {
                BattleId = reader.GetString(0),
                StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                Won = won,
                DamageDealt = (int)reader.GetInt64(4),
                ShipId = reader.GetString(5),
                DivisionSize = reader.GetInt32(6),
            });
        }

        return entries;
    }

    private static async Task<string> LoadSchemaAsync(CancellationToken cancellationToken)
    {
        // schema.sql 作为嵌入资源随程序集分发。
        var assembly = typeof(SqliteBattleRepository).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("schema.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("找不到嵌入的 schema.sql 资源。");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
