import type { Env } from "../types.js";

/**
 * 三级限额计数与概率性过期清理。
 *
 * 限额走 D1 单条条件 UPSERT，原子性由 SQLite 保证，不依赖先读后写的竞态窗口。
 */

/** UTC 日期字符串，与限额按日重置的口径一致。 */
function utcDay(nowMs = Date.now()): string {
  return new Date(nowMs).toISOString().slice(0, 10);
}

/**
 * 消耗一个限额计数单位。
 *
 * 单条 UPSERT，条件写法 rows_used + ? <= ? 保证并发下也不会超出限额：
 * 冲突时只有仍处于额度内才累加，超出则语句不生效，返回 false。
 * 全部走主键 (scope, key, day)，符合迁移 0002 的索引纪律。
 */
export async function consume(
  env: Env,
  scope: "device" | "ip" | "global",
  key: string,
  rows: number,
  limit: number,
): Promise<boolean> {
  const result = await env.DB.prepare(
    `INSERT INTO rate_limits (scope, key, day, rows_used)
     SELECT ?1, ?2, ?3, ?4
     WHERE ?4 <= ?5
     ON CONFLICT(scope, key, day) DO UPDATE SET
       rows_used = rows_used + ?4
     WHERE rows_used + ?4 <= ?5
     RETURNING rows_used`,
  )
    .bind(scope, key, utcDay(), rows, limit)
    .first<{ rows_used: number }>();

  return result !== null;
}

/**
 * 概率性过期清理。
 *
 * 5% 概率触发，避免每个请求都付清理成本；任何错误吞掉，不影响主流程。
 * rate_limits 与 rejection_log 保留 8 天，missing_tokens 过期 1 天后即可删。
 */
export async function ensureDailyCleanup(env: Env): Promise<void> {
  if (Math.random() >= 0.05) {
    return;
  }

  try {
    const now = Date.now();
    const staleMs = now - 8 * 24 * 3600 * 1000;
    const tokenStaleMs = now - 1 * 24 * 3600 * 1000;

    await env.DB.batch([
      env.DB.prepare("DELETE FROM rate_limits WHERE day < ?1").bind(
        new Date(staleMs).toISOString().slice(0, 10),
      ),
      env.DB.prepare("DELETE FROM rejection_log WHERE created_at < ?1").bind(staleMs),
      env.DB.prepare("DELETE FROM missing_tokens WHERE expires_at < ?1").bind(tokenStaleMs),
      // 过期缓存条目按最短 TTL（单船 3 天）加一倍缓冲清理，索引 idx_psc_captured 消费者。
      env.DB.prepare("DELETE FROM player_stats_cache WHERE captured_at < ?1").bind(
        now - 7 * 24 * 3600 * 1000,
      ),
    ]);
  } catch {
    // 清理失败不影响主流程，下个窗口再试。
  }
}
