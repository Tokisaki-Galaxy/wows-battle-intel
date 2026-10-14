import type { Env, PlayerStatsCache } from "../types.js";
import { json } from "../index.js";

/** 缓存条目的生存时间，秒。 */
const CACHE_TTL_SECONDS = 3600;

/**
 * 读取玩家统计缓存。
 *
 * KV 做热缓存，D1 做冷备份。KV 未命中时回查 D1 并回填 KV。
 */
export async function handleGetStats(accountId: string, env: Env): Promise<Response> {
  if (!accountId) {
    return json({ ok: false, error: "缺少账号 ID" }, 400);
  }

  // 先查 KV。
  const cached = await env.STATS_CACHE.get<PlayerStatsCache>(cacheKey(accountId), "json");
  if (cached) {
    return json({ ok: true, data: { ...cached, source: "kv" } });
  }

  // 回查 D1 聚合表。
  const row = await env.DB.prepare(
    `SELECT account_id, server, account_battles, account_wins,
            recent_battles, recent_wins, updated_at
     FROM player_stats WHERE account_id = ?1`,
  )
    .bind(accountId)
    .first<{
      account_id: string;
      server: string;
      account_battles: number;
      account_wins: number;
      recent_battles: number;
      recent_wins: number;
      updated_at: number;
    }>();

  if (!row) {
    return json({ ok: false, error: "未找到该玩家的统计" }, 404);
  }

  const stats: PlayerStatsCache = {
    accountId: row.account_id,
    server: row.server,
    accountBattles: row.account_battles,
    accountWinrate: row.account_battles > 0 ? row.account_wins / row.account_battles : 0,
    recent20Winrate: row.recent_battles > 0 ? row.recent_wins / row.recent_battles : 0,
    updatedAt: row.updated_at,
  };

  // 回填 KV。
  await env.STATS_CACHE.put(cacheKey(accountId), JSON.stringify(stats), {
    expirationTtl: CACHE_TTL_SECONDS,
  });

  return json({ ok: true, data: { ...stats, source: "d1" } });
}

/**
 * 写入或更新玩家统计缓存。
 *
 * 客户端在本地拉到新统计时回传，形成共享缓存。
 */
export async function handlePutStats(
  accountId: string,
  request: Request,
  env: Env,
): Promise<Response> {
  if (!accountId) {
    return json({ ok: false, error: "缺少账号 ID" }, 400);
  }

  const body = (await request.json()) as Partial<PlayerStatsCache>;
  if (typeof body.accountBattles !== "number" || typeof body.accountWinrate !== "number") {
    return json({ ok: false, error: "请求体缺少必要字段" }, 400);
  }

  const stats: PlayerStatsCache = {
    accountId,
    server: body.server ?? "Auto",
    accountBattles: body.accountBattles,
    accountWinrate: body.accountWinrate,
    recent20Winrate: body.recent20Winrate ?? -1,
    updatedAt: Date.now(),
  };

  await env.STATS_CACHE.put(cacheKey(accountId), JSON.stringify(stats), {
    expirationTtl: CACHE_TTL_SECONDS,
  });

  return json({ ok: true, data: stats });
}

/**
 * 构造 KV 键。加前缀避免与其他命名空间冲突。
 */
function cacheKey(accountId: string): string {
  return `stats:${accountId}`;
}
