-- ===========================================================================
-- wows-battle-intel 本地库结构
--
-- 统一约定：
-- - 时间戳一律 Unix 毫秒 UTC，存储为 INTEGER。
-- - 胜负胜率等统计字段用 REAL。
-- - 所有写入幂等，靠唯一约束保证。
-- ===========================================================================

-- 一局战斗的索引与结果。
CREATE TABLE IF NOT EXISTS battles (
    battle_id      TEXT    PRIMARY KEY,
    started_at     INTEGER NOT NULL,
    finished_at    INTEGER,
    map_id         TEXT    NOT NULL DEFAULT '',
    battle_type    TEXT    NOT NULL DEFAULT 'Unknown',
    game_version   TEXT    NOT NULL DEFAULT '',
    server         TEXT    NOT NULL DEFAULT 'Auto',
    result         TEXT    NOT NULL DEFAULT 'Unknown',
    ally_score     INTEGER NOT NULL DEFAULT 0,
    enemy_score    INTEGER NOT NULL DEFAULT 0,
    duration_sec   INTEGER NOT NULL DEFAULT 0,
    replay_path    TEXT,
    replay_parsed  INTEGER NOT NULL DEFAULT 0,
    created_at     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_battles_started_at ON battles (started_at);
CREATE INDEX IF NOT EXISTS idx_battles_type ON battles (battle_type);
CREATE INDEX IF NOT EXISTS idx_battles_server ON battles (server);

-- 一局中每个参战者的表现。
CREATE TABLE IF NOT EXISTS battle_players (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    battle_id        TEXT    NOT NULL,
    account_id       TEXT    NOT NULL,
    server           TEXT    NOT NULL DEFAULT 'Auto',
    player_name      TEXT    NOT NULL DEFAULT '',
    clan_tag         TEXT    NOT NULL DEFAULT '',
    ship_id          TEXT    NOT NULL DEFAULT '',
    ship_name        TEXT    NOT NULL DEFAULT '',
    ship_tier        INTEGER NOT NULL DEFAULT 0,
    ship_type        TEXT    NOT NULL DEFAULT 'Unknown',
    team             INTEGER NOT NULL DEFAULT 0,
    division_size    INTEGER NOT NULL DEFAULT 1,
    is_bot           INTEGER NOT NULL DEFAULT 0,
    is_hidden        INTEGER NOT NULL DEFAULT 0,
    damage_dealt     INTEGER NOT NULL DEFAULT 0,
    damage_taken     INTEGER NOT NULL DEFAULT 0,
    kills            INTEGER NOT NULL DEFAULT 0,
    spotting_damage  INTEGER NOT NULL DEFAULT 0,
    potential_damage INTEGER NOT NULL DEFAULT 0,
    survived         INTEGER NOT NULL DEFAULT 0,
    survived_sec     INTEGER NOT NULL DEFAULT 0,
    exp_earned       INTEGER NOT NULL DEFAULT 0,
    raw_json         TEXT,
    FOREIGN KEY (battle_id) REFERENCES battles (battle_id) ON DELETE CASCADE,
    UNIQUE (battle_id, account_id, server)
);

CREATE INDEX IF NOT EXISTS idx_bp_account ON battle_players (account_id);
CREATE INDEX IF NOT EXISTS idx_bp_account_battle ON battle_players (account_id, battle_id);
CREATE INDEX IF NOT EXISTS idx_bp_battle ON battle_players (battle_id);

-- 赛前预测记录，用于事后校准。
CREATE TABLE IF NOT EXISTS predictions (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    battle_id          TEXT    NOT NULL,
    predicted_at       INTEGER NOT NULL,
    model_version      TEXT    NOT NULL DEFAULT '',
    model_hash         TEXT    NOT NULL DEFAULT '',
    ally_win_prob      REAL    NOT NULL,
    confidence         REAL    NOT NULL DEFAULT 1.0,
    feature_json       TEXT,
    features_available INTEGER NOT NULL DEFAULT 1,
    actual_result      TEXT    NOT NULL DEFAULT 'Unknown',
    calibrated         INTEGER NOT NULL DEFAULT 0,
    created_at         INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_pred_battle ON predictions (battle_id);
CREATE INDEX IF NOT EXISTS idx_pred_predicted_at ON predictions (predicted_at);
CREATE INDEX IF NOT EXISTS idx_pred_calibration ON predictions (calibrated, actual_result);

-- 玩家统计的时间序列快照，近期状态特征的原料。
CREATE TABLE IF NOT EXISTS player_snapshots (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id          TEXT    NOT NULL,
    server              TEXT    NOT NULL DEFAULT 'Auto',
    captured_at         INTEGER NOT NULL,
    account_battles     INTEGER,
    account_winrate     REAL,
    account_avg_damage  REAL,
    account_winrate_solo REAL,
    account_battles_solo INTEGER,
    account_winrate_div2 REAL,
    account_battles_div2 INTEGER,
    account_winrate_div3 REAL,
    account_battles_div3 INTEGER,
    weighted_winrate    REAL,
    pr                  REAL,
    source              TEXT    NOT NULL DEFAULT '',
    class_stats         TEXT,
    UNIQUE (account_id, server, captured_at)
);

CREATE INDEX IF NOT EXISTS idx_snap_account_time
    ON player_snapshots (account_id, server, captured_at);

-- 各服务器的水平分布基准，用于胜率归一化。
CREATE TABLE IF NOT EXISTS server_baselines (
    server           TEXT PRIMARY KEY,
    computed_at      INTEGER NOT NULL,
    winrate_p25      REAL NOT NULL DEFAULT 0,
    winrate_p50      REAL NOT NULL DEFAULT 0,
    winrate_p75      REAL NOT NULL DEFAULT 0,
    winrate_mean     REAL NOT NULL DEFAULT 0,
    winrate_std      REAL NOT NULL DEFAULT 0,
    sample_size      INTEGER NOT NULL DEFAULT 0
);

-- 模式版本，用于将来做结构迁移。
CREATE TABLE IF NOT EXISTS schema_version (
    version    INTEGER PRIMARY KEY,
    applied_at INTEGER NOT NULL
);
