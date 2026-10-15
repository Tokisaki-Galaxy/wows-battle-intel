-- ===========================================================================
-- Migration 0002：协作缓存 v1
--
-- 新增四组设施：
-- 1. 设备注册与上报凭证（时隙承诺防女巫，见 docs/decisions/0008）
-- 2. 玩家统计共享缓存（账号级与单船级合一，分层 TTL 在读取端判断）
-- 3. missing token 一次性回传范围锁定
-- 4. 三级限额计数与拒绝日志
--
-- 纪律（沿用 0001）：所有查询必须走索引；每设备每日限额计数一行 UPSERT；
-- 拒绝日志七天后由读取端顺带清理，不单独跑清理任务。
-- ===========================================================================

-- 设备注册表。匿名即发，secret 只存哈希，泄露库不泄露凭证。
CREATE TABLE IF NOT EXISTS devices (
    device_id     TEXT    PRIMARY KEY,
    secret_hash   TEXT    NOT NULL,
    bound_account TEXT    NOT NULL DEFAULT '',
    trust_level   INTEGER NOT NULL DEFAULT 0,
    created_at    INTEGER NOT NULL,
    last_seen_at  INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_devices_bound ON devices (bound_account)
    WHERE bound_account != '';

-- 上报时隙去重。主键即幂等约束，同时隙同设备只收一次。
CREATE TABLE IF NOT EXISTS report_slots (
    device_id  TEXT    NOT NULL,
    slot       INTEGER NOT NULL,
    reported_at INTEGER NOT NULL,
    PRIMARY KEY (device_id, slot)
);

-- 玩家统计共享缓存。ship_id 为空串表示账号级快照，非空表示单船级。
-- class_stats 与军团分档 JSON 同样进 payload，一张表承载三种粒度。
CREATE TABLE IF NOT EXISTS player_stats_cache (
    account_id  TEXT    NOT NULL,
    server      TEXT    NOT NULL,
    ship_id     TEXT    NOT NULL DEFAULT '',
    payload     TEXT    NOT NULL,
    trust_level INTEGER NOT NULL DEFAULT 0,
    device_id   TEXT    NOT NULL DEFAULT '',
    captured_at INTEGER NOT NULL,
    PRIMARY KEY (account_id, server, ship_id)
);

CREATE INDEX IF NOT EXISTS idx_psc_captured ON player_stats_cache (captured_at DESC);

-- missing token。签发时记录声明范围，回传时验证行集合 ⊆ 声明集合。
CREATE TABLE IF NOT EXISTS missing_tokens (
    token        TEXT    PRIMARY KEY,
    device_id    TEXT    NOT NULL,
    accounts_json TEXT   NOT NULL,
    issued_at    INTEGER NOT NULL,
    expires_at   INTEGER NOT NULL,
    used_at      INTEGER
);

CREATE INDEX IF NOT EXISTS idx_mt_device ON missing_tokens (device_id, issued_at DESC);

-- 限额计数。scope: device | ip | global。key: 设备 ID、IP 哈希或常量 '*'。
-- 每天每键一行，UPSERT 累加，历史行由读取端顺带清理。
CREATE TABLE IF NOT EXISTS rate_limits (
    scope     TEXT    NOT NULL,
    key       TEXT    NOT NULL,
    day       TEXT    NOT NULL,
    rows_used INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (scope, key, day)
);

-- 拒绝日志。观察面，读取端写满即清七天前。
CREATE TABLE IF NOT EXISTS rejection_log (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    code       TEXT    NOT NULL,
    device_id  TEXT    NOT NULL DEFAULT '',
    ip_hash    TEXT    NOT NULL DEFAULT '',
    detail     TEXT    NOT NULL DEFAULT '',
    created_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_rejection_created ON rejection_log (created_at DESC);
