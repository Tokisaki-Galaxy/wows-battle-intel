import type {
  Env,
  PlayerQueryRequest,
  PlayerQueryResponse,
  PlayerReportRequest,
  PlayerStatsCache,
  RejectionCode,
} from "../types.js";
import { json } from "../index.js";
import {
  hmacHex,
  sha256Hex,
  verifySlotTicketWithHash,
  currentSlot,
  checkInvariants,
} from "../lib/verify.js";
import { consume, ensureDailyCleanup } from "../lib/ratelimit.js";

/**
 * 协作缓存 v1：批量查询与回传端点。
 *
 * 查询端点签发 missing token，回传端点以完整防线链路（票据、时隙、token、
 * 范围锁定、不变量、三级限额）收下未命中清单的数据。
 * SQL 纪律：查询侧一个 batch 完成全部点查，回传侧一个 batch 完成全部写入。
 */

/** 时隙长度，秒。与注册端点下发的 slotSeconds 保持一致。 */
const SLOT_SECONDS = 300;

/**
 * 服务端票据验收阈值：SHA256 前导零 bit 数下限。
 * 注册凭证里下发的推荐挖矿目标为 16，票据只看 bit 数下限，
 * 客户端挖得更深也能通过，验收取 12 兼顾边界余量与实际防女巫强度。
 */
const REPORT_DIFFICULTY = 12;

/** 每设备每日查询次数上限。 */
const QUERY_DAILY_LIMIT = 1000;

/** 单次查询键数上限，超过直接拒绝，防止 batch 无界。 */
const MAX_QUERY_KEYS = 32;

/** missing token 有效期：30 分钟。 */
const TOKEN_TTL_MS = 30 * 60 * 1000;

/** 账号级缓存 TTL：7 天。ship_id 为空串的条目。 */
const ACCOUNT_TTL_MS = 7 * 24 * 3600 * 1000;

/** 单船级缓存 TTL：3 天。数据抖动比账号级快，寿命更短。 */
const SHIP_TTL_MS = 3 * 24 * 3600 * 1000;

/** 回传三级限额：设备 / IP / 全站，每日行数。 */
const REPORT_DEVICE_DAILY_ROWS = 2000;
const REPORT_IP_DAILY_ROWS = 20_000;
const REPORT_GLOBAL_DAILY_ROWS = 80_000;

/**
 * 采样开关：拒绝日志 10% 采样、查询记账 10% 概率。
 * 测试注入 REJECT_SAMPLE_RATE = 1 使行为确定；生产默认 0.1。
 */
let rejectSampleRate = 0.1;

/** 测试钩子：固定采样率。 */
export function setRejectSampleRateForTest(rate: number): void {
  rejectSampleRate = rate;
}

/** 拒绝码到 HTTP 状态的映射。鉴权 401，格式类 400，业务防线 403，限额 429。 */
function rejectStatus(code: RejectionCode): number {  if (code === "E_AUTH") return 401;
  if (code === "E_LIMIT") return 429;
  if (
    code === "E_FORMAT" ||
    code === "E_RANGE" ||
    code === "E_CONSERVATION" ||
    code === "E_PLAUSIBLE" ||
    code === "E_TIME"
  ) {
    return 400;
  }
  return 403;
}

/** 写拒绝日志。观察面写入失败吞掉，不影响主流程的拒绝语义。 */
async function logRejection(
  env: Env,
  code: RejectionCode,
  deviceId: string,
  ipHash: string,
  detail: string,
): Promise<void> {
  // 采样写入：拒绝日志与核心缓存写共享 D1 每日写配额，
  // 零成本拒绝（E_FORMAT/E_AUTH）可被脚本刷出十万级请求，
  // 全量记录会把观察面变成 DoS 自己库的通道。默认 10% 采样足够看清攻击形态。
  if (Math.random() >= rejectSampleRate) {
    return;
  }
  try {
    await env.DB.prepare(
      "INSERT INTO rejection_log (code, device_id, ip_hash, detail, created_at) VALUES (?1, ?2, ?3, ?4, ?5)",
    )
      .bind(code, deviceId, ipHash, detail, Date.now())
      .run();
  } catch {
    // 日志是尽力而为，拒绝响应照常返回。
  }
}

/** 统一拒绝出口：先落日志再返回对应状态码。 */
async function reject(
  env: Env,
  code: RejectionCode,
  deviceId: string,
  ipHash: string,
  detail: string,
): Promise<Response> {
  await logRejection(env, code, deviceId, ipHash, detail);
  return json({ ok: false, error: `${code}: ${detail}` }, rejectStatus(code));
}

/** 计算 IP 哈希，与注册端点同口径。无代理头时回退空串。 */
async function ipHashOf(request: Request, env: Env): Promise<string> {
  const ip = request.headers.get("CF-Connecting-IP") ?? "";
  return sha256Hex(`${env.REPORT_SALT || ""}${ip}`);
}

/**
 * 批量查询端点：POST /api/v1/players/query
 *
 * 逐键主键点查 player_stats_cache，命中且在分层 TTL 内才算命中；
 * 未命中清单签发一次性 missing token，锁定后续回传的范围。
 */
export async function handlePlayerQuery(
  request: Request,
  env: Env,
  ctx: ExecutionContext,
): Promise<Response> {
  // 概率性过期清理在开头触发，函数内部自控 5% 概率；
  // 挂 waitUntil：响应返回后清理仍能跑完，不占请求延迟。
  ctx.waitUntil(ensureDailyCleanup(env));

  let body: PlayerQueryRequest;
  try {
    body = (await request.json()) as PlayerQueryRequest;
  } catch {
    return reject(env, "E_FORMAT", "", "", "请求体不是合法 JSON");
  }

  const ipHash = await ipHashOf(request, env);
  const deviceId = typeof body?.deviceId === "string" ? body.deviceId : "";
  const accounts = body?.accounts;

  if (
    !deviceId ||
    !Array.isArray(accounts) ||
    accounts.length > MAX_QUERY_KEYS ||
    accounts.some(
      (a) =>
        a === null ||
        typeof a !== "object" ||
        typeof a.accountId !== "string" ||
        typeof a.server !== "string",
    )
  ) {
    return reject(env, "E_FORMAT", deviceId, ipHash, "查询请求字段缺失或 accounts 超过 32 个");
  }

  const device = await env.DB.prepare("SELECT device_id FROM devices WHERE device_id = ?1")
    .bind(deviceId)
    .first<{ device_id: string }>();
  if (!device) {
    return reject(env, "E_AUTH", deviceId, ipHash, "设备未注册");
  }

  // 读接口轻限流：独立 key 前缀 q:，与回传额度互不挤占。
  // 计数本身是 D1 写，若每次查询都记账，百级设备即可烧穿每日写配额。
  // 概率记账：10% 概率写一行、每行记 10 次的量，统计上额度守恒而写量降九成。
  // 测试下采样率为 1，恒记账。
  if (Math.random() < rejectSampleRate) {
    const allowed = await consume(env, "device", `q:${deviceId}`, 10, QUERY_DAILY_LIMIT);
    if (!allowed) {
      return json({ ok: false, error: "E_LIMIT: 今日查询次数已用尽" }, 429);
    }
  }

  // 归一化查询键：shipId 缺省为空串即账号级
  const keys = accounts.map((a) => ({
    accountId: a.accountId,
    server: a.server,
    shipId: typeof a.shipId === "string" ? a.shipId : "",
  }));

  // 逐键主键点查：一个 batch 一次往返，每条都是全索引命中
  const selects = keys.map((k) =>
    env.DB.prepare(
      "SELECT payload, captured_at FROM player_stats_cache WHERE account_id = ?1 AND server = ?2 AND ship_id = ?3",
    ).bind(k.accountId, k.server, k.shipId),
  );
  const results = await env.DB.batch(selects);

  const hits: PlayerQueryResponse["hits"] = [];
  const missing: PlayerQueryResponse["missing"] = [];
  const now = Date.now();
  for (let i = 0; i < keys.length; i++) {
    const key = keys[i];
    const result = results[i] as { results?: Array<{ payload: string; captured_at: number }> } | undefined;
    const row = result?.results?.[0];
    if (!key) continue;

    let payload: PlayerStatsCache | null = null;
    if (row) {
      try {
        payload = JSON.parse(row.payload) as PlayerStatsCache;
      } catch {
        // 缓存条目损坏按未命中处理，等新数据覆盖。
      }
    }
    const ttl = key.shipId === "" ? ACCOUNT_TTL_MS : SHIP_TTL_MS;
    if (payload && row && now - row.captured_at < ttl) {
      hits.push({ ...key, payload });
    } else {
      // 过期条目视为 miss 但不删，留给清理任务
      missing.push({ ...key });
    }
  }

  let missingToken = "";
  if (missing.length > 0) {
    // HMAC(REPORT_SALT, deviceId + 清单 + 毫秒时间戳) 截断 32 字符：
    // 同设备同清单同毫秒幂等，不引入额外随机源，也不依赖 UUID 的唯一性保证
    missingToken = (await hmacHex(env.REPORT_SALT, deviceId + JSON.stringify(missing) + now)).slice(0, 32);
    await env.DB.prepare(
      `INSERT INTO missing_tokens (token, device_id, accounts_json, issued_at, expires_at, used_at)
       VALUES (?1, ?2, ?3, ?4, ?5, NULL)
       ON CONFLICT(token) DO NOTHING`,
    )
      .bind(missingToken, deviceId, JSON.stringify(missing), now, now + TOKEN_TTL_MS)
      .run();
  }

  return json({ ok: true, data: { hits, missing, missingToken } satisfies PlayerQueryResponse });
}

/**
 * 回传端点：POST /api/v1/players/report
 *
 * 防线链路：E_AUTH（票据）→ E_SLOT（时隙窗口与去重）→ E_TOKEN（一次性 token）
 * → E_SCOPE（范围锁定）→ 不变量 → 三级限额，全部通过后单批写入。
 * captured_at 一律取服务端时间，客户端自报的 updatedAt 忽略。
 */
export async function handlePlayerReport(request: Request, env: Env): Promise<Response> {
  const ipHash = await ipHashOf(request, env);

  let body: PlayerReportRequest;
  try {
    body = (await request.json()) as PlayerReportRequest;
  } catch {
    return reject(env, "E_FORMAT", "", ipHash, "请求体不是合法 JSON");
  }

  const deviceId = typeof body?.deviceId === "string" ? body.deviceId : "";
  const slot = body?.slot;
  const nonce = typeof body?.nonce === "string" ? body.nonce : "";
  const missingToken = typeof body?.missingToken === "string" ? body.missingToken : "";
  const rows = Array.isArray(body?.rows) ? body.rows : null;

  if (
    !deviceId ||
    typeof slot !== "number" ||
    !Number.isInteger(slot) ||
    !nonce ||
    // nonce 限长：合法票据的 nonce 远小于此，防大字符串拖慢哈希。
    nonce.length > 128 ||
    !missingToken ||
    !rows ||
    rows.length === 0 ||
    // 行数硬上限：一局最多 24 名玩家乘账号级与单船级两层，32 已足够。
    // 无上限时单请求可灌 10 万行，配合限额首写窗口能击穿全局限额。
    rows.length > 32
  ) {
    return reject(env, "E_FORMAT", deviceId, ipHash, "回传请求字段缺失或 rows 超限");
  }

  // 1. 设备存在 + 票据验证。服务端只持 secret_hash，挖矿输入即该 hash，明文 secret 全程不出现。
  const device = await env.DB.prepare("SELECT secret_hash FROM devices WHERE device_id = ?1")
    .bind(deviceId)
    .first<{ secret_hash: string }>();
  if (!device) {
    return reject(env, "E_AUTH", deviceId, ipHash, "设备未注册");
  }
  const ticketOk = await verifySlotTicketWithHash(deviceId, device.secret_hash, slot, nonce, REPORT_DIFFICULTY);
  if (!ticketOk) {
    return reject(env, "E_AUTH", deviceId, ipHash, "时隙票据验证未通过");
  }

  // 2. 时隙窗口：允许上一时隙，防跨秒边界；同时隙同设备只收一次
  const now = Date.now();
  const current = currentSlot(now, SLOT_SECONDS);
  if (slot !== current && slot !== current - 1) {
    return reject(env, "E_SLOT", deviceId, ipHash, "时隙不在当前或上一窗口内");
  }
  const slotOccupied = await env.DB.prepare(
    "SELECT device_id FROM report_slots WHERE device_id = ?1 AND slot = ?2",
  )
    .bind(deviceId, slot)
    .first<{ device_id: string }>();
  if (slotOccupied) {
    return reject(env, "E_SLOT", deviceId, ipHash, "同时隙重复回传");
  }

  // 3. token 校验与核销原子化：UPDATE ... WHERE used_at IS NULL RETURNING，
  // 并发双请求只有一个能拿到行，一次性语义无 TOCTOU 窗口。
  const token = await env.DB.prepare(
    `UPDATE missing_tokens SET used_at = ?2
     WHERE token = ?1 AND used_at IS NULL AND expires_at > ?3
     RETURNING device_id, accounts_json`,
  )
    .bind(missingToken, now, now)
    .first<{ device_id: string; accounts_json: string }>();
  if (!token || token.device_id !== deviceId) {
    return reject(env, "E_TOKEN", deviceId, ipHash, "missing token 无效、过期或已使用");
  }

  // 4+5+6. 范围锁定与不变量单趟过：行集合必须 ⊆ token 声明清单，且每行通过六条不变量
  let scope: Set<string>;
  try {
    scope = new Set(
      (JSON.parse(token.accounts_json) as PlayerQueryResponse["missing"]).map(
        (e) => `${e.accountId}|${e.server}|${e.shipId}`,
      ),
    );
  } catch {
    scope = new Set();
  }

  for (let i = 0; i < rows.length; i++) {
    const row = rows[i];
    if (
      !row ||
      typeof row.accountId !== "string" ||
      typeof row.server !== "string" ||
      typeof row.shipId !== "string" ||
      typeof row.payload !== "object" ||
      row.payload === null
    ) {
      return reject(env, "E_FORMAT", deviceId, ipHash, `第 ${i} 行字段类型不对`);
    }
    if (!scope.has(`${row.accountId}|${row.server}|${row.shipId}`)) {
      return reject(env, "E_SCOPE", deviceId, ipHash, `第 ${i} 行超出 token 声明范围`);
    }
    const code = checkInvariants(row.payload, row.accountId, row.server, row.shipId);
    if (code) {
      return reject(env, code, deviceId, ipHash, `第 ${i} 行不变量校验失败`);
    }
  }

  // 7. 三级限额：设备 / IP / 全站，按行数计数。任一超限立即拒绝。
  if (!(await consume(env, "device", deviceId, rows.length, REPORT_DEVICE_DAILY_ROWS))) {
    return reject(env, "E_LIMIT", deviceId, ipHash, "设备每日回传行数超限");
  }
  if (!(await consume(env, "ip", `ip:${ipHash}`, rows.length, REPORT_IP_DAILY_ROWS))) {
    return reject(env, "E_LIMIT", deviceId, ipHash, "IP 每日回传行数超限");
  }
  if (!(await consume(env, "global", "*", rows.length, REPORT_GLOBAL_DAILY_ROWS))) {
    return reject(env, "E_LIMIT", deviceId, ipHash, "全站每日回传行数超限");
  }

  // 8. 单批写入：行 UPSERT + 时隙占位 + token 核销，一次往返。
  //    captured_at 取服务端时间，ON CONFLICT 条件保证旧时间戳覆盖不了新数据。
  const statements = rows.map((row) =>
    env.DB.prepare(
      `INSERT INTO player_stats_cache (account_id, server, ship_id, payload, trust_level, device_id, captured_at)
       VALUES (?1, ?2, ?3, ?4, 0, ?5, ?6)
       ON CONFLICT(account_id, server, ship_id) DO UPDATE SET
         payload = excluded.payload,
         device_id = excluded.device_id,
         captured_at = excluded.captured_at
       WHERE excluded.captured_at > player_stats_cache.captured_at`,
    ).bind(row.accountId, row.server, row.shipId, JSON.stringify(row.payload), deviceId, now),
  );
  statements.push(
    env.DB.prepare(
      "INSERT INTO report_slots (device_id, slot, reported_at) VALUES (?1, ?2, ?3) ON CONFLICT(device_id, slot) DO NOTHING",
    ).bind(deviceId, slot, now),
  );
  // token 已在第 3 步原子核销，此处无需再动。
  await env.DB.batch(statements);

  return json({ ok: true, data: { accepted: rows.length, slot } });
}
