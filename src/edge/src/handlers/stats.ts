import type { Env, PlayerStatsCache } from "../types.js";
import { CACHE_TTL_SECONDS, RECENT_WINDOW, RESULT_WIN, STATS_CACHE_PREFIX } from "../constants.js";
import { fail, json, readJson } from "../http.js";

/**
 * 读取玩家统计缓存。
 *
 * KV 做热缓存，D1 做冷备份。KV 未命中时回查 D1 并回填 KV。
 */
export async function handleGetStats(accountId: string, env: Env): Promise<Response> {
  if (!accountId) {
    return fail(400, "缺少账号 ID");
  }

  // 先查 KV。
  const cached = await env.STATS_CACHE.get<PlayerStatsCache>(cacheKey(accountId), "json");
  if (cached) {
    return json({ ok: true, data: { ...cached, source: "kv" } });
  }

  // 回查 D1 聚合表。同一账号可能出现在多个服，取最近更新的一行。
  const row = await env.DB.prepare(
    `SELECT account_id, server, account_battles, account_wins, updated_at
     FROM player_stats WHERE account_id = ?1
     ORDER BY updated_at DESC LIMIT 1`,
  )
    .bind(accountId)
    .first<{
      account_id: string;
      server: string;
      account_battles: number;
      account_wins: number;
      updated_at: number;
    }>();

  if (!row) {
    return fail(404, "未找到该玩家的统计");
  }

  const recent = await computeRecentWinrate(env, accountId, row.server);

  const stats: PlayerStatsCache = {
    accountId: row.account_id,
    server: row.server,
    accountBattles: row.account_battles,
    accountWinrate: row.account_battles > 0 ? row.account_wins / row.account_battles : 0,
    recent20Winrate: recent,
    updatedAt: row.updated_at,
  };

  // 回填 KV。
  await env.STATS_CACHE.put(cacheKey(accountId), JSON.stringify(stats), {
    expirationTtl: CACHE_TTL_SECONDS,
  });

  return json({ ok: true, data: { ...stats, source: "d1" } });
}

/**
 * 按逐场记录计算最近 {@link RECENT_WINDOW} 场的胜率。
 *
 * 聚合表的 recent_battles / recent_wins 是无上限累加，等同全生涯计数，
 * 不能当作近 20 场使用。这里直接按时间取窗口内的逐场结果。样本不足
 * 或查询失败时返回 -1，与客户端的“样本不足”哨兵一致。
 */
async function computeRecentWinrate(
  env: Env,
  accountId: string,
  server: string,
): Promise<number> {
  const { results } = await env.DB.prepare(
    `SELECT b.result AS result
     FROM player_battle_records p
     JOIN battles b ON b.battle_id = p.battle_id
     WHERE p.account_id = ?1 AND p.server = ?2
     ORDER BY b.started_at DESC
     LIMIT ?3`,
  )
    .bind(accountId, server, RECENT_WINDOW)
    .all<{ result: string }>();

  const rows = results ?? [];
  if (rows.length === 0) {
    return -1;
  }

  const wins = rows.filter((r) => r.result === RESULT_WIN).length;
  return wins / rows.length;
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
    return fail(400, "缺少账号 ID");
  }

  const parsed = await readJson(request);
  if (parsed === null || typeof parsed !== "object") {
    return fail(400, "请求体不是合法 JSON");
  }

  const body = parsed as Partial<PlayerStatsCache>;
  const battles = body.accountBattles;
  const winrate = body.accountWinrate;
  if (
    typeof battles !== "number" ||
    !Number.isFinite(battles) ||
    battles < 0 ||
    typeof winrate !== "number" ||
    !Number.isFinite(winrate) ||
    winrate < 0 ||
    winrate > 1
  ) {
    return fail(400, "请求体缺少必要字段或取值越界");
  }

  const recent = body.recent20Winrate;
  const stats: PlayerStatsCache = {
    accountId,
    server: typeof body.server === "string" ? body.server : "Auto",
    accountBattles: battles,
    accountWinrate: winrate,
    recent20Winrate:
      typeof recent === "number" && Number.isFinite(recent) && recent >= 0 && recent <= 1
        ? recent
        : -1,
    updatedAt: Date.now(),
  };

  await env.STATS_CACHE.put(cacheKey(accountId), JSON.stringify(stats), {
    expirationTtl: CACHE_TTL_SECONDS,
  });

  return json({ ok: true, data: stats });
}

/**
 * 构造 KV 键。前缀见 {@link STATS_CACHE_PREFIX}。
 */
function cacheKey(accountId: string): string {
  return `${STATS_CACHE_PREFIX}${accountId}`;
}
