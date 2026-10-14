-- ===========================================================================
-- 中央协调库 D1 结构
--
-- 设计原则（见 ADR 0002）：
-- - D1 单库 10GB 上限，逐场明细是增长主力，预留迁出路径。
-- - D1 按行读取计费，所有查询必须走索引，禁止全表扫描。
-- - 云端只存可索引的查询字段，明细压缩或推 R2。
-- ===========================================================================

-- 对局索引。只保留可查询的字段，明细推 R2。
CREATE TABLE IF NOT EXISTS battles (
    battle_id        TEXT    PRIMARY KEY,
    started_at       INTEGER NOT NULL,
    map_id           TEXT    NOT NULL DEFAULT '',
    battle_type      TEXT    NOT NULL DEFAULT 'Unknown',
    server           TEXT    NOT NULL DEFAULT 'Auto',
    game_version     TEXT    NOT NULL DEFAULT '',
    result           TEXT    NOT NULL DEFAULT 'Unknown',
    ally_score       INTEGER NOT NULL DEFAULT 0,
    enemy_score      INTEGER NOT NULL DEFAULT 0,
    duration_sec     INTEGER NOT NULL DEFAULT 0,
    contributor_hash TEXT    NOT NULL DEFAULT '',
    created_at       INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_battles_started ON battles (started_at DESC);
CREATE INDEX IF NOT EXISTS idx_battles_type_server ON battles (battle_type, server);
CREATE INDEX IF NOT EXISTS idx_battles_contributor ON battles (contributor_hash);

-- 逐场玩家记录。增长主力，按时间分区或整体迁出到 R2。
CREATE TABLE IF NOT EXISTS player_battle_records (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    battle_id     TEXT    NOT NULL,
    account_id    TEXT    NOT NULL,
    server        TEXT    NOT NULL DEFAULT 'Auto',
    ship_id       TEXT    NOT NULL DEFAULT '',
    team          INTEGER NOT NULL DEFAULT 0,
    division_size INTEGER NOT NULL DEFAULT 1,
    damage_dealt  INTEGER NOT NULL DEFAULT 0,
    kills         INTEGER NOT NULL DEFAULT 0,
    survived      INTEGER NOT NULL DEFAULT 0,
    exp_earned    INTEGER NOT NULL DEFAULT 0,
    recorded_at   INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_pbr_account ON player_battle_records (account_id, battle_id);
CREATE INDEX IF NOT EXISTS idx_pbr_battle ON player_battle_records (battle_id);
CREATE INDEX IF NOT EXISTS idx_pbr_recorded ON player_battle_records (recorded_at DESC);

-- 玩家统计的长期聚合。逐场明细之外的另一份，供快速查询。
CREATE TABLE IF NOT EXISTS player_stats (
    account_id       TEXT    NOT NULL,
    server           TEXT    NOT NULL,
    updated_at       INTEGER NOT NULL,
    account_battles  INTEGER NOT NULL DEFAULT 0,
    account_wins     INTEGER NOT NULL DEFAULT 0,
    recent_battles   INTEGER NOT NULL DEFAULT 0,
    recent_wins      INTEGER NOT NULL DEFAULT 0,
    last_battle_at   INTEGER,
    PRIMARY KEY (account_id, server)
);

CREATE INDEX IF NOT EXISTS idx_ps_updated ON player_stats (updated_at DESC);

-- 各服务器的水平分布基准，用于胜率归一化。
CREATE TABLE IF NOT EXISTS server_baselines (
    server       TEXT PRIMARY KEY,
    computed_at  INTEGER NOT NULL,
    winrate_p25  REAL NOT NULL DEFAULT 0,
    winrate_p50  REAL NOT NULL DEFAULT 0,
    winrate_p75  REAL NOT NULL DEFAULT 0,
    winrate_mean REAL NOT NULL DEFAULT 0,
    winrate_std  REAL NOT NULL DEFAULT 0,
    sample_size  INTEGER NOT NULL DEFAULT 0
);

-- 模型版本登记。
CREATE TABLE IF NOT EXISTS models (
    model_version TEXT    PRIMARY KEY,
    r2_key        TEXT    NOT NULL DEFAULT '',
    sha256        TEXT    NOT NULL DEFAULT '',
    trained_at    INTEGER NOT NULL,
    metrics_json  TEXT    NOT NULL DEFAULT '{}',
    active        INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_models_active ON models (active, trained_at DESC);

-- 上报幂等去重。避免同一批数据重复入库。
CREATE TABLE IF NOT EXISTS upload_dedup (
    batch_id     TEXT PRIMARY KEY,
    received_at  INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_dedup_received ON upload_dedup (received_at DESC);
