using Microsoft.Data.Sqlite;
using WowsBattleIntel.Core.Abstractions;
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
public sealed class SqliteBattleRepository : IBattleRepository, IPlayerSnapshotStore
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
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string schema = await LoadSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
            // 相同账号同一时间戳只保留一条。
            command.CommandText = """
                INSERT INTO player_snapshots (
                    account_id, server, captured_at,
                    account_battles, account_winrate,
                    account_winrate_solo, account_battles_solo,
                    account_winrate_div2, account_battles_div2,
                    account_winrate_div3, account_battles_div3,
                    weighted_winrate, pr, source
                ) VALUES (
                    $accountId, $server, $capturedAt,
                    $battles, $winrate,
                    $wrSolo, $bSolo,
                    $wrDiv2, $bDiv2,
                    $wrDiv3, $bDiv3,
                    $weighted, NULL, $source
                )
                ON CONFLICT(account_id, server, captured_at) DO NOTHING;
                """;

            command.Parameters.AddWithValue("$accountId", s.AccountId);
            command.Parameters.AddWithValue("$server", s.Server.ToString());
            command.Parameters.AddWithValue("$capturedAt", s.CapturedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$battles", s.AccountBattles);
            command.Parameters.AddWithValue("$winrate", s.AccountWinrate);
            command.Parameters.AddWithValue("$wrSolo", s.AccountWinrateSolo);
            command.Parameters.AddWithValue("$bSolo", s.AccountBattlesSolo);
            command.Parameters.AddWithValue("$wrDiv2", s.AccountWinrateDiv2);
            command.Parameters.AddWithValue("$bDiv2", s.AccountBattlesDiv2);
            command.Parameters.AddWithValue("$wrDiv3", s.AccountWinrateDiv3);
            command.Parameters.AddWithValue("$bDiv3", s.AccountBattlesDiv3);
            command.Parameters.AddWithValue("$weighted", s.WeightedWinrate);
            command.Parameters.AddWithValue("$source", s.Source ?? string.Empty);

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

        return new PlayerSnapshot
        {
            AccountId = reader.GetString(reader.GetOrdinal("account_id")),
            Server = Enum.TryParse<GameServer>(
                reader.GetString(reader.GetOrdinal("server")), out var sv) ? sv : GameServer.Auto,
            CapturedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                reader.GetInt64(reader.GetOrdinal("captured_at"))),
            AccountBattles = reader.GetInt32(reader.GetOrdinal("account_battles")),
            AccountWinrate = reader.GetDouble(reader.GetOrdinal("account_winrate")),
            AccountWinrateSolo = reader.GetDouble(reader.GetOrdinal("account_winrate_solo")),
            AccountBattlesSolo = reader.GetInt32(reader.GetOrdinal("account_battles_solo")),
            AccountWinrateDiv2 = reader.GetDouble(reader.GetOrdinal("account_winrate_div2")),
            AccountBattlesDiv2 = reader.GetInt32(reader.GetOrdinal("account_battles_div2")),
            AccountWinrateDiv3 = reader.GetDouble(reader.GetOrdinal("account_winrate_div3")),
            AccountBattlesDiv3 = reader.GetInt32(reader.GetOrdinal("account_battles_div3")),
            WeightedWinrate = reader.GetDouble(reader.GetOrdinal("weighted_winrate")),
        };
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
