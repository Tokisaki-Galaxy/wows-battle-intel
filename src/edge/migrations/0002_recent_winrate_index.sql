-- ===========================================================================
-- 近期胜率查询的索引
--
-- 读取侧按账号取最近 N 场逐场结果（stats.ts 的 computeRecentWinrate），
-- 需要在 account_id 上按对局开始时间倒序取窗口。原有 idx_pbr_account
-- 的排序键是 battle_id，走不到时间序，这里补一个覆盖账号与大类的索引。
-- ===========================================================================

CREATE INDEX IF NOT EXISTS idx_pbr_account_server
    ON player_battle_records (account_id, server, battle_id);
