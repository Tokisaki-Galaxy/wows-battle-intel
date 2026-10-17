using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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
public sealed class SqliteBattleRepository : IBattleRepository, IBattleListProvider, IPlayerSnapshotStore, IPlayerHistoryProvider, IClanGradeCache, IPlayerStatsCache
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
    /// 一次性取出库里全部对局的开战时间。
    /// </summary>
    /// <remarks>
    /// 观测器首次扫描要逐份判断回放是否已入库。每份回放都走一次 SQL 会让
    /// 几百份文件产生几百次往返，这里一次读进内存比对。单场对局一行。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>库里全部对局的开战时间。</returns>
    public async Task<IReadOnlyList<DateTimeOffset>> GetStartedAtValuesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT started_at FROM battles;";

        var values = new List<DateTimeOffset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)));
        }

        return values;
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

        // 单船统计与常用等级各列。逐列独立检查，塞在同一个代码块里时
        // 旧库已带 ship_battles 会跳过整块，后续新增列永远加不上。
        await AddColumnIfMissing(existing, connection, "ship_id", "TEXT");
        await AddColumnIfMissing(existing, connection, "ship_battles", "INTEGER");
        await AddColumnIfMissing(existing, connection, "ship_winrate", "REAL");
        await AddColumnIfMissing(existing, connection, "ship_avg_damage", "REAL");
        await AddColumnIfMissing(existing, connection, "favorite_tier", "INTEGER");

        // 军团标签快照列。身份字段，让缓存加速路径也能把标签灌回名单。
        await AddColumnIfMissing(existing, connection, "clan_tag", "TEXT");

        // 单船层快照的查询索引。引用 ship_id，必须在加列之后建，
        // 因此放迁移里而 schema.sql 只服务新库也不重复建。
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_snap_account_ship
                    ON player_snapshots (account_id, server, ship_id, captured_at);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 组队标识列。人数只够区分单排与多排，同一局里两个双人队会撞色，
        // 界面按小队着色需要原始 division_id。旧库补列后旧行保持 NULL。
        var battlePlayerColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(battle_players);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                battlePlayerColumns.Add(reader.GetString(1));
            }
        }

        await AddColumnIfMissing(battlePlayerColumns, connection, "battle_players", "division_id", "TEXT");

        // 逐船伤害明细与成就列。两列都是回放解析的衍生数据，只做展示，
        // 旧行补列后保持 NULL，读取时按空列表处理。
        await AddColumnIfMissing(battlePlayerColumns, connection, "battle_players", "damage_by_target", "TEXT");
        await AddColumnIfMissing(battlePlayerColumns, connection, "battle_players", "achievements", "TEXT");

        // 军团分档缓存表。存量库走迁移补建，新库由 schema.sql 直接创建。
        bool hasClanGrades = false;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='clan_grades';";
            hasClanGrades = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
        }

        if (!hasClanGrades)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS clan_grades (
                    tag           TEXT    PRIMARY KEY,
                    grade         TEXT    NOT NULL DEFAULT 'Normal',
                    name          TEXT    NOT NULL DEFAULT '',
                    members_count INTEGER NOT NULL DEFAULT 0,
                    winrate       REAL    NOT NULL DEFAULT -1,
                    queried_at    INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>列不存在时加列，已存在则跳过。迁移幂等，重复执行安全。</summary>
    private static Task AddColumnIfMissing(
        HashSet<string> existing,
        SqliteConnection connection,
        string columnName,
        string columnType)
        => AddColumnIfMissing(existing, connection, "player_snapshots", columnName, columnType);

    /// <summary>按指定表加列，已存在则跳过。</summary>
    private static async Task AddColumnIfMissing(
        HashSet<string> existing,
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnType)
    {
        if (existing.Contains(columnName))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        // 表名、列名与类型来自调用方字面量，不来自用户输入，拼接安全。
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnType};";
        await command.ExecuteNonQueryAsync();
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
                    division_id,
                    is_bot, is_hidden, damage_dealt, damage_taken, kills,
                    spotting_damage, potential_damage, survived, survived_sec,
                    exp_earned, damage_by_target, achievements, raw_json
                ) VALUES (
                    $battleId, $accountId, $server, $name, $clan,
                    $shipId, $shipName, $tier, $shipType, $team, $div,
                    $divId,
                    $bot, $hidden, $dmgDealt, $dmgTaken, $kills,
                    $spot, $potential, $survived, $survivedSec,
                    $exp, $dmgByTarget, $achievements, NULL
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
            command.Parameters.AddWithValue("$divId", (object?)p.DivisionId ?? DBNull.Value);
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
            command.Parameters.AddWithValue("$dmgByTarget",
                SerializeDamageByTarget(p.DamageByTarget));
            command.Parameters.AddWithValue("$achievements", SerializeAchievements(p.Achievements));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 存量参战者行的船信息与身份字段回填。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SaveBattleAsync"/> 的先删后插不同，这里按身份主键
    /// (battle_id, account_id, server) 逐行 UPDATE，只覆盖舰船与身份列，
    /// 对局表现列保持库中原值。用于修正存量库把 ship_id 存成内部索引码
    /// 的历史数据，配合 rebuild-players 命令消费。
    /// </remarks>
    /// <returns>实际更新的行数，身份不匹配的参战者不计入。</returns>
    public async Task<int> UpdatePlayerShipInfoAsync(
        string battleId,
        IReadOnlyList<PlayerRecord> players,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(battleId);
        ArgumentNullException.ThrowIfNull(players);
        if (players.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        int updated = 0;
        foreach (var p in players)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE battle_players SET
                    ship_id       = $shipId,
                    ship_name     = $shipName,
                    ship_tier     = $tier,
                    ship_type     = $shipType,
                    clan_tag      = $clan,
                    player_name   = $name,
                    division_size = $div,
                    division_id   = $divId,
                    is_bot        = $bot,
                    is_hidden     = $hidden
                WHERE battle_id = $battleId
                  AND account_id = $accountId
                  AND server = $server;
                """;

            command.Parameters.AddWithValue("$shipId", p.ShipId ?? string.Empty);
            command.Parameters.AddWithValue("$shipName", p.ShipName ?? string.Empty);
            command.Parameters.AddWithValue("$tier", p.ShipTier);
            command.Parameters.AddWithValue("$shipType", p.ShipClass.ToStorageString());
            command.Parameters.AddWithValue("$clan", p.ClanTag ?? string.Empty);
            command.Parameters.AddWithValue("$name", p.PlayerName ?? string.Empty);
            command.Parameters.AddWithValue("$div", p.DivisionSize);
            command.Parameters.AddWithValue("$divId", (object?)p.DivisionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$bot", p.IsBot ? 1 : 0);
            command.Parameters.AddWithValue("$hidden", p.IsHidden ? 1 : 0);
            command.Parameters.AddWithValue("$battleId", battleId);
            command.Parameters.AddWithValue("$accountId", p.AccountId);
            command.Parameters.AddWithValue("$server", p.Server.ToString());

            updated += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
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
    /// 一次取出列表要展示的字段，左侧列表启动时只需要这么多。
    /// </summary>
    /// <remarks>
    /// 单条 SQL 取完列表，避免旧实现那样逐局查预测再加名单：两千行就是
    /// 四千次往返。等级走 battle_players 的聚合子查询，只取友方最大等级，
    /// 不读明细行；预测取该局最新的一条，与详情区展示口径一致。
    /// </remarks>
    /// <param name="limit">最多返回多少局。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按开战时间倒序的列表项。</returns>
    public async Task<IReadOnlyList<BattleListEntry>> GetBattleListAsync(
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
            WITH tier AS (
                SELECT battle_id, MAX(ship_tier) AS ally_tier
                FROM battle_players
                WHERE team = 0
                GROUP BY battle_id
            ),
            latest_prediction AS (
                SELECT battle_id, ally_win_prob
                FROM (
                    SELECT battle_id, ally_win_prob,
                           ROW_NUMBER() OVER (PARTITION BY battle_id ORDER BY predicted_at DESC) AS rn
                    FROM predictions
                )
                WHERE rn = 1
            )
            SELECT b.battle_id, b.started_at, b.map_id, b.battle_type, b.server,
                   b.result, b.duration_sec,
                   COALESCE(t.ally_tier, 0) AS ally_tier,
                   p.ally_win_prob,
                   EXISTS(SELECT 1 FROM battle_players bp WHERE bp.battle_id = b.battle_id) AS has_players
            FROM battles b
            LEFT JOIN tier t ON t.battle_id = b.battle_id
            LEFT JOIN latest_prediction p ON p.battle_id = b.battle_id
            ORDER BY b.started_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<BattleListEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadListEntry(reader));
        }

        return entries;
    }

    private static BattleListEntry ReadListEntry(SqliteDataReader reader)
    {
        string battleId = reader.GetString(0);
        return new BattleListEntry(
            battleId,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Enum.TryParse<BattleType>(reader.GetString(3), out var bt) ? bt : BattleType.Unknown,
            Enum.TryParse<GameServer>(reader.GetString(4), out var sv) ? sv : GameServer.Auto,
            Enum.TryParse<BattleOutcome>(reader.GetString(5), out var oc) ? oc : BattleOutcome.Unknown,
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetDouble(8),
            battleId.EndsWith(":pre", StringComparison.Ordinal),
            reader.GetInt64(9) != 0);
    }

    /// <summary>
    /// 列出库里全部赛前占位记录。
    /// </summary>
    /// <remarks>
    /// 占位记录指后端带 :pre 的标识。取全量而不是某一页：清理判据要对整库
    /// 成立，按页码取会让截断之外的占位记录永远清不掉。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部占位记录的摘要。</returns>
    public async Task<IReadOnlyList<BattleListEntry>> GetPrebattlePlaceholdersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT battle_id, started_at, map_id, battle_type, server,
                   result, duration_sec, 0 AS ally_tier,
                   NULL AS ally_win_prob,
                   EXISTS(SELECT 1 FROM battle_players bp WHERE bp.battle_id = b.battle_id) AS has_players
            FROM battles b
            WHERE substr(b.battle_id, -4) = ':pre'
            ORDER BY started_at DESC;
            """;

        var entries = new List<BattleListEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadListEntry(reader));
        }

        return entries;
    }

    /// <summary>
    /// 批量移除赛前占位记录，连同其参战者与预测。
    /// </summary>
    /// <remarks>
    /// 同一局的占位记录（标识带 :pre 后缀）在正式回放记录已经存在时应当消失，
    /// 否则列表会出现两条几乎相同的条目。只删占位记录，且调用方负责保证
    /// 同局已有正式记录；这里不做等价判定，避免把两局不同战斗误删。
    /// 返回被删除的占位记录数量。
    /// </remarks>
    /// <param name="placeholderIds">要删除的占位记录标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<int> DeletePrebattlePlaceholdersAsync(
        IReadOnlyCollection<string> placeholderIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placeholderIds);
        var ids = placeholderIds
            .Where(id => id.EndsWith(":pre", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        int removed = 0;
        foreach (string id in ids)
        {
            foreach (string table in new[] { "battle_players", "predictions", "battles" })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                // 表名来自上面的字面量集合，不来自外部输入。
                command.CommandText = $"DELETE FROM {table} WHERE battle_id = $id;";
                command.Parameters.AddWithValue("$id", id);
                int affected = await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                removed += table == "battles" ? affected : 0;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
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
    /// 按昵称批量查已入库的真实账号 ID，供赛前名单命中缓存。
    /// </summary>
    /// <remarks>
    /// 赛前名单的 vehicles.id 是会话临时 ID，解析期一律置 -1 哨兵，真账号
    /// 要按昵称解析。历史回放里同一玩家早已带真账号入库，先用本地历史把
    /// 昵称换成账号，可省一次 account/list 请求，也能让账号级快照直接命中
    /// 缓存。WG 昵称在同一服务器内唯一，同名取最近一条。
    /// </remarks>
    /// <returns>昵称到账号 ID 的映射，查不到的昵称不在其中。</returns>
    public async Task<Dictionary<string, string>> GetAccountIdsByNamesAsync(
        IReadOnlyList<string> playerNames,
        GameServer server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playerNames);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = playerNames
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            return result;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // 分批查询避免参数过多。每批一次 IN 查询，取账号非哨兵的最近记录。
        const int batchSize = 100;
        for (int offset = 0; offset < names.Count; offset += batchSize)
        {
            var batch = names.Skip(offset).Take(batchSize).ToList();
            await using var command = connection.CreateCommand();
            var placeholders = string.Join(",", batch.Select((_, i) => $"$n{i}"));
            // 每个昵称取最新一条带真账号的记录。窗口函数按 id 倒序取第一行，
            // 结果确定；同名多账号的极端情况取最近一次入库的那个。
            command.CommandText = $"""
                SELECT player_name, account_id FROM (
                    SELECT player_name, account_id,
                           ROW_NUMBER() OVER (PARTITION BY player_name ORDER BY id DESC) AS rn
                    FROM battle_players
                    WHERE player_name IN ({placeholders})
                      AND account_id <> '-1' AND account_id <> ''
                      AND server = $server
                ) WHERE rn = 1;
                """;
            for (int i = 0; i < batch.Count; i++)
            {
                command.Parameters.AddWithValue($"$n{i}", batch[i]);
            }

            command.Parameters.AddWithValue("$server", server.ToString());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string name = reader.GetString(0);
                string accountId = reader.GetString(1);
                result[name] = accountId;
            }
        }

        return result;
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
        return await GetSnapshotAsync(accountId, server, shipId: null, DateTimeOffset.MinValue, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 查账号在指定服务器上指定船只的最新一条带单船数据的快照。
    /// </summary>
    /// <remarks>
    /// 单船数据与快照行绑定，同账号换船会落多条带不同 ship_id 的快照。
    /// 这里按船维度取最新一条，换船后历史单船数据仍可命中，不再重复补拉。
    /// </remarks>
    public async Task<PlayerSnapshot?> GetLatestShipSnapshotAsync(
        string accountId,
        GameServer server,
        string shipId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shipId);
        return await GetSnapshotAsync(accountId, server, shipId, DateTimeOffset.MinValue, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 快照查询的公共实现。shipId 为空查账号级最新快照，否则查该船最新一条。
    /// </summary>
    /// <remarks>
    /// minCapturedAt 为时间下界，只返回该时刻之后采集的快照。取
    /// <see cref="DateTimeOffset.MinValue"/> 时不设下界，取任意最新一条。
    /// 缓存加速查询靠它实现 TTL 语义。
    /// </remarks>
    private async Task<PlayerSnapshot?> GetSnapshotAsync(
        string accountId,
        GameServer server,
        string? shipId,
        DateTimeOffset minCapturedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // 时间下界与 ship_id 两个条件排列组合，四段 SQL 内联，避免动态拼条件。
        // captured_at 用毫秒整数比较，与写入口径一致。
        bool hasLowerBound = minCapturedAt > DateTimeOffset.MinValue;
        if (string.IsNullOrEmpty(shipId))
        {
            command.CommandText = hasLowerBound
                ? """
                  SELECT * FROM player_snapshots
                  WHERE account_id = $accountId AND server = $server
                    AND captured_at >= $minCapturedAt
                  ORDER BY captured_at DESC
                  LIMIT 1;
                  """
                : """
                  SELECT * FROM player_snapshots
                  WHERE account_id = $accountId AND server = $server
                  ORDER BY captured_at DESC
                  LIMIT 1;
                  """;
        }
        else
        {
            // ship_battles >= 0 过滤掉 ship_id 为空或单船数据缺失的行。
            command.CommandText = hasLowerBound
                ? """
                  SELECT * FROM player_snapshots
                  WHERE account_id = $accountId AND server = $server
                    AND ship_id = $shipId AND ship_battles >= 0
                    AND captured_at >= $minCapturedAt
                  ORDER BY captured_at DESC
                  LIMIT 1;
                  """
                : """
                  SELECT * FROM player_snapshots
                  WHERE account_id = $accountId AND server = $server
                    AND ship_id = $shipId AND ship_battles >= 0
                  ORDER BY captured_at DESC
                  LIMIT 1;
                  """;
            command.Parameters.AddWithValue("$shipId", shipId);
        }

        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$server", server.ToString());
        if (hasLowerBound)
        {
            command.Parameters.AddWithValue("$minCapturedAt", minCapturedAt.ToUnixTimeMilliseconds());
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadSnapshot(reader);
    }

    /// <inheritdoc />
    public async Task<PlayerSnapshot?> GetAccountSnapshotAsync(
        string accountId,
        GameServer server,
        TimeSpan maxAge,
        CancellationToken cancellationToken = default)
    {
        return await GetSnapshotAsync(accountId, server, shipId: null, ThresholdFrom(maxAge), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PlayerSnapshot?> GetShipSnapshotAsync(
        string accountId,
        GameServer server,
        string shipId,
        TimeSpan maxAge,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shipId);
        return await GetSnapshotAsync(accountId, server, shipId, ThresholdFrom(maxAge), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 把缓存有效期换算成快照的时间下界。
    /// </summary>
    /// <remarks>
    /// <see cref="TimeSpan.MaxValue"/> 或负值表示不设下界，返回
    /// <see cref="DateTimeOffset.MinValue"/> 让查询取任意最新一条。
    /// </remarks>
    private static DateTimeOffset ThresholdFrom(TimeSpan maxAge) =>
        maxAge == TimeSpan.MaxValue || maxAge < TimeSpan.Zero
            ? DateTimeOffset.MinValue
            : DateTimeOffset.UtcNow - maxAge;

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
                    weighted_winrate, pr, source, class_stats, clan_tag,
                    ship_id, ship_battles, ship_winrate, ship_avg_damage, favorite_tier
                ) VALUES (
                    $accountId, $server, $capturedAt,
                    $battles, $winrate, $avgDamage,
                    $wrSolo, $bSolo,
                    $wrDiv2, $bDiv2,
                    $wrDiv3, $bDiv3,
                    $weighted, NULL, $source, $classStats, $clanTag,
                    $shipId, $shipBattles, $shipWinrate, $shipAvgDamage, $favoriteTier
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
            command.Parameters.AddWithValue("$clanTag", s.ClanTag ?? string.Empty);
            var classStats = command.Parameters.Add("$classStats", SqliteType.Text);
            classStats.Value = string.IsNullOrEmpty(s.ClassStatsJson)
                ? DBNull.Value
                : s.ClassStatsJson;
            var shipId = command.Parameters.Add("$shipId", SqliteType.Text);
            shipId.Value = string.IsNullOrEmpty(s.ShipId) ? DBNull.Value : s.ShipId;
            command.Parameters.AddWithValue("$shipBattles", s.ShipBattles);
            command.Parameters.AddWithValue("$shipWinrate", s.ShipWinrate);
            command.Parameters.AddWithValue("$shipAvgDamage", s.ShipAvgDamage);
            command.Parameters.AddWithValue("$favoriteTier", s.FavoriteTier);

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

            // 军团标签列，旧快照无此列或为 NULL 时落空串。
            ClanTag = TryGetString(reader, "clan_tag"),

            // 单船三列，旧快照与账号级快照均无值，落回 -1 哨兵。
            ShipId = shipIdOrdinal >= 0 && !reader.IsDBNull(shipIdOrdinal)
                ? reader.GetString(shipIdOrdinal)
                : string.Empty,
            ShipBattles = TryGetDouble(reader, "ship_battles"),
            ShipWinrate = TryGetDouble(reader, "ship_winrate"),
            ShipAvgDamage = TryGetDouble(reader, "ship_avg_damage"),
            FavoriteTier = (int)TryGetDouble(reader, "favorite_tier"),
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

    /// <summary>按列名取文本，列不存在或为 NULL 时返回空串。</summary>
    private static string TryGetString(SqliteDataReader reader, string name)
    {
        int ordinal = TryGetOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
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
    public async Task<IReadOnlySet<string>> GetSnapshotAccountKeysSinceAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT account_id || ':' || server
            FROM player_snapshots
            WHERE captured_at >= $since;
            """;
        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());

        var keys = new HashSet<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    /// <inheritdoc />
    public async Task<int> DeleteSnapshotsOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM player_snapshots WHERE captured_at < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CleanupSnapshotsAsync(
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        // 0 与负值表示不清理，直接返回。
        if (retentionDays <= 0)
        {
            return 0;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        return await DeleteSnapshotsOlderThanAsync(cutoff, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>军团分档缓存的有效期。</summary>
    private static readonly TimeSpan ClanGradeTtl = TimeSpan.FromDays(7);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, ClanGradeInfo>> GetClanGradesAsync(
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count == 0)
        {
            return new Dictionary<string, ClanGradeInfo>(StringComparer.Ordinal);
        }

        long oldestAllowed = DateTimeOffset.UtcNow
            .Subtract(ClanGradeTtl)
            .ToUnixTimeMilliseconds();

        var result = new Dictionary<string, ClanGradeInfo>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 标签集合来自调用方去重后的名单，逐个参数化避免拼接注入。
        var clauses = new List<string>(tags.Count);
        for (int i = 0; i < tags.Count; i++)
        {
            clauses.Add($"$tag{i}");
            command.Parameters.AddWithValue($"$tag{i}", tags[i]);
        }

        command.CommandText = $"""
            SELECT tag, grade, name, members_count, winrate
            FROM clan_grades
            WHERE tag IN ({string.Join(", ", clauses)}) AND queried_at >= $oldest;
            """;
        command.Parameters.AddWithValue("$oldest", oldestAllowed);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var info = new ClanGradeInfo
            {
                Tag = reader.GetString(0),
                Grade = Enum.TryParse<ClanGradeTier>(reader.GetString(1), out var g)
                    ? g
                    : ClanGradeTier.Normal,
                Name = reader.GetString(2),
                MembersCount = reader.GetInt32(3),
                AggregateWinrate = reader.GetDouble(4),
            };
            result[info.Tag] = info;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task SaveClanGradesAsync(
        IReadOnlyList<ClanGradeInfo> grades,
        DateTimeOffset queriedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grades);
        if (grades.Count == 0)
        {
            return;
        }

        long now = queriedAt.ToUnixTimeMilliseconds();
        long oldestAllowed = DateTimeOffset.UtcNow
            .Subtract(ClanGradeTtl)
            .ToUnixTimeMilliseconds();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var g in grades)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO clan_grades (tag, grade, name, members_count, winrate, queried_at)
                VALUES ($tag, $grade, $name, $members, $winrate, $queriedAt)
                ON CONFLICT(tag) DO UPDATE SET
                    grade         = excluded.grade,
                    name          = excluded.name,
                    members_count = excluded.members_count,
                    winrate       = excluded.winrate,
                    queried_at    = excluded.queried_at;
                """;

            command.Parameters.AddWithValue("$tag", g.Tag.ToUpperInvariant());
            command.Parameters.AddWithValue("$grade", g.Grade.ToString());
            command.Parameters.AddWithValue("$name", g.Name ?? string.Empty);
            command.Parameters.AddWithValue("$members", g.MembersCount);
            command.Parameters.AddWithValue("$winrate", g.AggregateWinrate);
            command.Parameters.AddWithValue("$queriedAt", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 写入顺带清理过期行，缓存表不会无限膨胀。
        await using var cleanup = connection.CreateCommand();
        cleanup.Transaction = (SqliteTransaction)transaction;
        cleanup.CommandText = "DELETE FROM clan_grades WHERE queried_at < $oldest;";
        cleanup.Parameters.AddWithValue("$oldest", oldestAllowed);
        await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
            DivisionId = reader.IsDBNull(reader.GetOrdinal("division_id"))
                ? null
                : reader.GetString(reader.GetOrdinal("division_id")),
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

            // 逐船伤害明细与成就。坏 JSON 只丢这一行这两个字段，不能连累整局列表。
            DamageByTarget = DeserializeDamageByTarget(reader, "damage_by_target"),
            Achievements = DeserializeAchievements(reader),
        };
    }

    /// <summary>
    /// 逐船伤害明细的落盘 JSON 选项。
    /// </summary>
    /// <remarks>
    /// 字段名用小写驼峰，与库内其余 JSON 列保持一致。枚举按名字写出，
    /// 免得将来调整 <see cref="ShipClass"/> 的顺序时把旧数据读错。
    /// </remarks>
    private static readonly JsonSerializerOptions DamageByTargetJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>把逐船伤害明细序列化成 JSON。空列表写 null，与旧行同形。</summary>
    private static object SerializeDamageByTarget(IReadOnlyList<DamageByTarget>? items)
    {
        if (items is null || items.Count == 0)
        {
            return DBNull.Value;
        }

        return JsonSerializer.Serialize(items, DamageByTargetJsonOptions);
    }

    /// <summary>把成就展示文本序列化成 JSON 数组。空列表写 null。</summary>
    private static object SerializeAchievements(IReadOnlyList<string>? items)
    {
        if (items is null || items.Count == 0)
        {
            return DBNull.Value;
        }

        return JsonSerializer.Serialize(items, DamageByTargetJsonOptions);
    }

    /// <summary>
    /// 读取逐船伤害明细。列缺失、为 NULL 或 JSON 损坏时返回空列表。
    /// </summary>
    /// <remarks>
    /// 这两列是派生数据，损坏了也不影响整局的可读性。为保证单行坏数据
    /// 不炸掉整个参战者列表，解析失败一律静默降级为空，只留一行调试输出。
    /// </remarks>
    private static IReadOnlyList<DamageByTarget> DeserializeDamageByTarget(
        SqliteDataReader reader,
        string columnName)
    {
        string? json = TryGetString(reader, columnName);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<DamageByTarget>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<DamageByTarget>>(json, DamageByTargetJsonOptions)
                ?? (IReadOnlyList<DamageByTarget>)Array.Empty<DamageByTarget>();
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[SqliteBattleRepository] 逐船伤害明细解析失败，按空列表处理：{ex.Message}");
            return Array.Empty<DamageByTarget>();
        }
    }

    /// <summary>读取成就展示文本。列缺失、为 NULL 或 JSON 损坏时返回空列表。</summary>
    private static IReadOnlyList<string> DeserializeAchievements(SqliteDataReader reader)
    {
        string? json = TryGetString(reader, "achievements");
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, DamageByTargetJsonOptions)
                ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[SqliteBattleRepository] 成就列表解析失败，按空列表处理：{ex.Message}");
            return Array.Empty<string>();
        }
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
