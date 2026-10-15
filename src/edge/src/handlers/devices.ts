import type { DeviceCredential, Env } from "../types.js";
import { json } from "../index.js";
import { hmacHex, constantTimeEqual, sha256Hex } from "../lib/verify.js";
import { consume } from "../lib/ratelimit.js";

/**
 * 设备注册端点。
 *
 * 匿名即发凭证，secret 只存 HMAC 哈希，服务端无法还原明文。
 * 幂等语义：客户端已有凭证时携带 deviceId 与 secret 重注册，
 * 校验通过后原样返回相同凭证，避免重复入库。
 */

/** 上报挖矿难度：SHA256 前导零 bit 数，与服务端 verifySlotTicket 的判定口径一致。 */
const REGISTER_DIFFICULTY = 16;

/** 时隙长度，秒。 */
const SLOT_SECONDS = 300;

/** 同一 IP 每天最多注册的设备数。 */
const REGISTER_IP_DAILY_LIMIT = 50;

/** 生成的 secret 字节数，hex 编码后 64 字符。 */
const SECRET_BYTES = 32;

/** 生成本次注册的凭证。deviceId 由客户端提供或随机生成，secret 恒为随机。 */
async function buildCredential(env: Env, deviceId?: string): Promise<DeviceCredential> {
  const secretBytes = new Uint8Array(SECRET_BYTES);
  crypto.getRandomValues(secretBytes);
  let secret = "";
  for (const byte of secretBytes) {
    secret += byte.toString(16).padStart(2, "0");
  }
  // secretHash 随凭证一并下发：客户端挖矿只用 hash，secret 明文留作将来 HMAC 通信。
  const secretHash = await hmacHex(env.REPORT_SALT, secret);
  return {
    deviceId: deviceId ?? crypto.randomUUID(),
    secret,
    secretHash,
    difficulty: REGISTER_DIFFICULTY,
    slotSeconds: SLOT_SECONDS,
  };
}

/**
 * 设备注册处理。
 *
 * SQL 支出：限额 1 条 + 幂等查重 1 条 + 入库 1 条，符合免费档 CPU 预算。
 */
export async function handleDeviceRegister(request: Request, env: Env): Promise<Response> {
  let body: { deviceId?: unknown; secret?: unknown };
  try {
    body = (await request.json()) as typeof body;
  } catch {
    return json({ ok: false, error: "请求体不是合法 JSON" }, 400);
  }

  const ipHash = await ipHashOf(request, env);

  // 限流在一切之前：注册端点匿名开放，IP 限额是唯一的滥用闸门
  const allowed = await consume(env, "ip", `reg:${ipHash}`, 1, REGISTER_IP_DAILY_LIMIT);
  if (!allowed) {
    return json({ ok: false, error: "注册过于频繁，请明日再试" }, 429);
  }

  const hasExisting = typeof body.deviceId === "string" && body.deviceId.length > 0;

  if (hasExisting) {
    const deviceId = body.deviceId as string;
    const secret = typeof body.secret === "string" ? body.secret : "";

    if (!secret) {
      return json({ ok: false, error: "重注册必须携带 secret" }, 400);
    }

    const existing = await env.DB.prepare(
      "SELECT secret_hash FROM devices WHERE device_id = ?1",
    )
      .bind(deviceId)
      .first<{ secret_hash: string }>();

    if (!existing) {
      // 客户端声称有凭证但库里查无此设备，按全新注册处理并沿用其 deviceId
      const credential = await buildCredential(env, deviceId);
      await insertDevice(env, credential);
      return json({ ok: true, data: credential });
    }

    const candidateHash = await hmacHex(env.REPORT_SALT, secret);
    if (!constantTimeEqual(candidateHash, existing.secret_hash)) {
      return json({ ok: false, error: "凭证校验失败" }, 403);
    }

    // secret 明文服务端不存，此处原样返回客户端自带的 secret，hash 用注册时同口径重算
    return json({
      ok: true,
      data: {
        deviceId,
        secret,
        secretHash: candidateHash,
        difficulty: REGISTER_DIFFICULTY,
        slotSeconds: SLOT_SECONDS,
      } satisfies DeviceCredential,
    });
  }

  const credential = await buildCredential(env);
  await insertDevice(env, credential);
  return json({ ok: true, data: credential });
}

/** 设备入库。secret 只存 HMAC(REPORT_SALT, secret)，明文绝不落库。 */
async function insertDevice(env: Env, credential: DeviceCredential): Promise<void> {
  const now = Date.now();
  const secretHash = await hmacHex(env.REPORT_SALT, credential.secret);
  await env.DB.prepare(
    `INSERT INTO devices (device_id, secret_hash, bound_account, trust_level, created_at, last_seen_at)
     VALUES (?1, ?2, '', 0, ?3, ?3)
     ON CONFLICT(device_id) DO NOTHING`,
  )
    .bind(credential.deviceId, secretHash, now)
    .run();
}

/** 计算 IP 哈希。无代理头时回退空串，哈希仍可计算，限流退化为全站共享额度。 */
async function ipHashOf(request: Request, env: Env): Promise<string> {
  const ip = request.headers.get("CF-Connecting-IP") ?? "";
  return sha256Hex(`${env.REPORT_SALT || ""}${ip}`);
}
