import type { PlayerStatsCache, RejectionCode } from "../types.js";

/**
 * 协作缓存 v1 的验证库。
 *
 * 承载上报链路的密码学原语与数据不变量校验。
 * 所有哈希基于 WebCrypto，worker 与 Node 18+ 均原生可用，无第三方依赖。
 */

/** 将 ArrayBuffer 转为小写十六进制字符串。 */
function toHex(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let hex = "";
  for (const byte of bytes) {
    hex += byte.toString(16).padStart(2, "0");
  }
  return hex;
}

/** SHA256，十六进制小写输出。 */
export async function sha256Hex(data: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(data));
  return toHex(digest);
}

/** HMAC-SHA256，密钥为 secret，十六进制小写输出。 */
export async function hmacHex(secret: string, data: string): Promise<string> {
  const key = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const signature = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(data));
  return toHex(signature);
}

/**
 * 验证上报时隙票据（协作缓存 v1 服务端入口）。
 *
 * 客户端挖矿输入是注册响应下发的 secretHash（即 HMAC(REPORT_SALT, secret)），
 * 服务端用库内 secret_hash 直接验证，全程不接触 secret 明文。
 * 票据公式：SHA256(deviceId || secretHash || slot || nonce) 的前导零 bit 数不低于 difficulty。
 * 逐字节统计前导零，首字节的高位参与计数，避免按字符比较带来的位对齐错误。
 */
export async function verifySlotTicketWithHash(
  deviceId: string,
  secretHash: string,
  slot: number,
  nonce: string,
  difficulty: number,
): Promise<boolean> {
  const digest = new Uint8Array(
    await crypto.subtle.digest(
      "SHA-256",
      new TextEncoder().encode(`${deviceId}${secretHash}${slot}${nonce}`),
    ),
  );

  let zeroBits = 0;
  for (const byte of digest) {
    if (byte === 0) {
      zeroBits += 8;
      continue;
    }
    // clz32 对单字节值恒为 24 + 字节内前导零数
    zeroBits += Math.clz32(byte) - 24;
    break;
  }
  return zeroBits >= difficulty;
}

/**
 * 通用票据校验原语，语义等价于 verifySlotTicketWithHash。
 *
 * 保留独立入口供既有调用方沿用旧签名；输入串的实际含义由调用方约定，
 * 协作缓存 v1 的服务端验证一律走 verifySlotTicketWithHash 并传入 secretHash。
 */
export async function verifySlotTicket(
  deviceId: string,
  secret: string,
  slot: number,
  nonce: string,
  difficulty: number,
): Promise<boolean> {
  return verifySlotTicketWithHash(deviceId, secret, slot, nonce, difficulty);
}

/** 当前所处时隙序号。时隙由 epoch 起算，全局对齐，客户端与服务端无需交换时钟。 */
export function currentSlot(nowMs: number, slotSeconds: number): number {
  return Math.floor(nowMs / 1000 / slotSeconds);
}

/** 服务端允许的区服枚举。 */
const VALID_SERVERS: readonly string[] = ["RU", "EU", "NA", "ASIA", "CTA"];

/** 账号场次的合理上限，超过视为伪造。 */
const MAX_ACCOUNT_BATTLES = 1_000_000;

/**
 * 上报数据六条不变量校验。全部通过返回 null，否则返回拒绝码。
 *
 * payload 类型只声明了核心字段，但历史上允许携带
 * accountWins、recentBattles、recentWins 等扩展计数，
 * 守恒检查经由索引访问这些可选字段，类型不符时跳过该项检查。
 */
export function checkInvariants(
  payload: PlayerStatsCache,
  accountId: string,
  server: string,
  shipId: string,
): RejectionCode | null {
  // 格式：账号 ID 纯数字、区服在枚举内、核心字段类型正确
  if (!/^\d+$/.test(accountId)) {
    return "E_FORMAT";
  }
  if (!VALID_SERVERS.includes(server)) {
    return "E_FORMAT";
  }
  if (typeof shipId !== "string") {
    return "E_FORMAT";
  }
  if (
    typeof payload.accountBattles !== "number" ||
    !Number.isInteger(payload.accountBattles) ||
    typeof payload.accountWinrate !== "number" ||
    typeof payload.recent20Winrate !== "number" ||
    typeof payload.updatedAt !== "number"
  ) {
    return "E_FORMAT";
  }

  // 范围：胜率落在 0..1，-1 是未知哨兵，允许通过
  for (const winrate of [payload.accountWinrate, payload.recent20Winrate]) {
    if (winrate !== -1 && (!Number.isFinite(winrate) || winrate < 0 || winrate > 1)) {
      return "E_RANGE";
    }
  }

  const extra = payload as unknown as Record<string, unknown>;
  const asNum = (value: unknown): number | null =>
    typeof value === "number" && Number.isFinite(value) ? value : null;

  // 守恒：场次不为负，wins 不超过对应 battles，近期场次不超过总场次
  if (payload.accountBattles < 0) {
    return "E_CONSERVATION";
  }
  const accountWins = asNum(extra["accountWins"]);
  if (accountWins !== null && accountWins > payload.accountBattles) {
    return "E_CONSERVATION";
  }
  const recentBattles = asNum(extra["recentBattles"]);
  if (recentBattles !== null) {
    if (recentBattles < 0) {
      return "E_CONSERVATION";
    }
    // recent20Winrate 为哨兵时近期窗口无意义，跳过该项
    if (payload.recent20Winrate !== -1 && recentBattles > payload.accountBattles) {
      return "E_CONSERVATION";
    }
  }
  const recentWins = asNum(extra["recentWins"]);
  if (recentWins !== null && recentWins > (recentBattles ?? payload.accountBattles)) {
    return "E_CONSERVATION";
  }

  // 单船级条目（shipId 非空）可携带 shipBattles/shipWins 扩展字段：
  // 单船场次不得超过账号总场次，单船胜场不得超过单船场次。
  if (shipId !== "") {
    const shipBattles = asNum(extra["shipBattles"]);
    if (shipBattles !== null) {
      if (shipBattles < 0 || shipBattles > payload.accountBattles) {
        return "E_CONSERVATION";
      }
      const shipWins = asNum(extra["shipWins"]);
      if (shipWins !== null && (shipWins < 0 || shipWins > shipBattles)) {
        return "E_CONSERVATION";
      }
    }
  }

  // 合理性：总场次超出人类极限视为伪造
  if (payload.accountBattles >= MAX_ACCOUNT_BATTLES) {
    return "E_PLAUSIBLE";
  }

  return null;
}

/**
 * 常数时间字符串比较。
 *
 * 长度差先并入累加器，再按较长一方定长循环异或，
 * 越界 charCodeAt 返回 NaN，ToInt32 后为 0，不短路，防止时序侧信道。
 */
export function constantTimeEqual(a: string, b: string): boolean {
  let diff = a.length ^ b.length;
  const maxLen = Math.max(a.length, b.length);
  for (let i = 0; i < maxLen; i++) {
    diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  }
  return diff === 0;
}
