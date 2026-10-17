-- ===========================================================================
-- 近期胜率查询的索引
--
-- 读取侧按账号取最近 N 场逐场结果（stats.ts 的 computeRecentWinrate），
-- 过滤条件是 account_id 与 server 的等值匹配。原有 idx_pbr_account 只覆盖
-- account_id 与 battle_id，补上 server 后这条查询全程走索引。
--
-- 注意：窗口的排序键是 battles.started_at，不在本表里，排序仍需回表。
-- 当前窗口只有 20 行，回表代价可忽略；若将来窗口放大，需要把对局时间
-- 冗余到本表再建 (account_id, server, battle_started_at) 复合索引。
-- ===========================================================================

CREATE INDEX IF NOT EXISTS idx_pbr_account_server
    ON player_battle_records (account_id, server, battle_id);
