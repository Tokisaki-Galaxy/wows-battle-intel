import { describe, it, expect } from "vitest";
import {
  sha256Hex,
  hmacHex,
  verifySlotTicket,
  currentSlot,
  checkInvariants,
  constantTimeEqual,
} from "../src/lib/verify.js";
import type { PlayerStatsCache } from "../src/types.js";

/** 构造一份默认合法的上报载荷，个别用例按需覆盖字段。 */
function validPayload(overrides: Partial<PlayerStatsCache> = {}): PlayerStatsCache {
  return {
    accountId: "1001",
    server: "ASIA",
    accountBattles: 5000,
    accountWinrate: 0.52,
    recent20Winrate: 0.55,
    updatedAt: Date.now(),
    ...overrides,
  };
}

/** 暴力找一个能让 SHA256(deviceId+secret+slot+nonce) 满足难度的 nonce。 */
async function mineNonce(
  deviceId: string,
  secret: string,
  slot: number,
  difficulty: number,
): Promise<string> {
  for (let i = 0; i < 5_000_000; i++) {
    const nonce = i.toString(36);
    if (await verifySlotTicket(deviceId, secret, slot, nonce, difficulty)) {
      return nonce;
    }
  }
  throw new Error("挖矿失败，难度过高或实现有误");
}

/** 暴力找一个前导零 bit 数恰好等于 zeroBits 的 nonce，用于边界精度验证。 */
async function mineExactZeroBits(
  deviceId: string,
  secret: string,
  slot: number,
  zeroBits: number,
): Promise<string> {
  for (let i = 0; i < 5_000_000; i++) {
    const nonce = i.toString(36);
    if (await verifySlotTicket(deviceId, secret, slot, nonce, zeroBits)) {
      // 要求恰好：再高一档难度必须失败
      if (!(await verifySlotTicket(deviceId, secret, slot, nonce, zeroBits + 1))) {
        return nonce;
      }
    }
  }
  throw new Error("未找到恰好满足前导零位数的 nonce");
}

describe("哈希原语", () => {
  it("sha256Hex 输出小写十六进制且长度 64", async () => {
    const hex = await sha256Hex("abc");
    expect(hex).toMatch(/^[0-9a-f]{64}$/);
    // 与公开测试向量比对
    expect(hex).toBe(
      "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
    );
  });

  it("hmacHex 与已知 RFC 测试向量一致", async () => {
    // RFC 4231 Test Case 2: HMAC-SHA256(key="Jefe", data="what do ya want for nothing?")
    const hex = await hmacHex("Jefe", "what do ya want for nothing?");
    expect(hex).toBe(
      "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843",
    );
  });
});

describe("verifySlotTicket", () => {
  const deviceId = "device-001";
  const secret = "s3cret";
  const slot = 123456;

  it("满足难度的票据通过", async () => {
    const nonce = await mineNonce(deviceId, secret, slot, 12);
    expect(await verifySlotTicket(deviceId, secret, slot, nonce, 12)).toBe(true);
  });

  it("任意输入不满足难度则失败", async () => {
    expect(await verifySlotTicket(deviceId, secret, slot, "not-a-mined-nonce", 12)).toBe(
      false,
    );
  });

  it("输入任一分量改变即失败", async () => {
    const nonce = await mineNonce(deviceId, secret, slot, 8);
    expect(await verifySlotTicket("device-002", secret, slot, nonce, 8)).toBe(false);
    expect(await verifySlotTicket(deviceId, "wrong", slot, nonce, 8)).toBe(false);
    expect(await verifySlotTicket(deviceId, secret, slot + 1, nonce, 8)).toBe(false);
  });

  it("逐字节前导零计数正确跨字节边界", async () => {
    // 构造：恰好 9 bit 前导零的票据。9 跨越首字节全部 8 位与次字节最高位，
    // 若按字节整数比较会漏掉跨字节情况，这里验证非 8 倍数难度的精确判定。
    const nonce = await mineExactZeroBits(deviceId, secret, slot, 9);
    expect(await verifySlotTicket(deviceId, secret, slot, nonce, 9)).toBe(true);
    expect(await verifySlotTicket(deviceId, secret, slot, nonce, 10)).toBe(false);
  });
});

describe("currentSlot", () => {
  it("按 epoch 对齐切分时隙", () => {
    expect(currentSlot(0, 300)).toBe(0);
    expect(currentSlot(299_999, 300)).toBe(0);
    expect(currentSlot(300_000, 300)).toBe(1);
    // 1 秒时隙便于验证边界翻转
    expect(currentSlot(999, 1)).toBe(0);
    expect(currentSlot(1000, 1)).toBe(1);
    // 2026-10-16T00:00:00Z = 1792211200000ms，除以 300000
    expect(currentSlot(1792211200000, 300)).toBe(5974037);
  });
});

describe("checkInvariants", () => {
  it("合法载荷返回 null", () => {
    expect(checkInvariants(validPayload(), "1001", "ASIA", "")).toBeNull();
    expect(checkInvariants(validPayload(), "1001", "EU", "PASS710")).toBeNull();
  });

  it("E_FORMAT：账号 ID 非纯数字", () => {
    expect(checkInvariants(validPayload(), "10o1", "ASIA", "")).toBe("E_FORMAT");
    expect(checkInvariants(validPayload(), "", "ASIA", "")).toBe("E_FORMAT");
    expect(checkInvariants(validPayload(), "-5", "ASIA", "")).toBe("E_FORMAT");
  });

  it("E_FORMAT：区服不在枚举内", () => {
    expect(checkInvariants(validPayload(), "1001", "asia", "")).toBe("E_FORMAT");
    expect(checkInvariants(validPayload(), "1001", "CN", "")).toBe("E_FORMAT");
  });

  it("E_FORMAT：字段类型不对", () => {
    const bad = validPayload() as unknown as Record<string, unknown>;
    bad.accountBattles = "5000";
    expect(
      checkInvariants(bad as unknown as PlayerStatsCache, "1001", "ASIA", ""),
    ).toBe("E_FORMAT");

    const bad2 = validPayload() as unknown as Record<string, unknown>;
    bad2.updatedAt = null;
    expect(
      checkInvariants(bad2 as unknown as PlayerStatsCache, "1001", "ASIA", ""),
    ).toBe("E_FORMAT");
  });

  it("E_RANGE：胜率越界", () => {
    expect(
      checkInvariants(validPayload({ accountWinrate: 1.2 }), "1001", "ASIA", ""),
    ).toBe("E_RANGE");
    expect(
      checkInvariants(validPayload({ recent20Winrate: -0.5 }), "1001", "ASIA", ""),
    ).toBe("E_RANGE");
  });

  it("E_RANGE：-1 哨兵允许通过", () => {
    expect(
      checkInvariants(
        validPayload({ accountWinrate: -1, recent20Winrate: -1 }),
        "1001",
        "ASIA",
        "",
      ),
    ).toBeNull();
  });

  it("E_CONSERVATION：总场次为负", () => {
    expect(
      checkInvariants(validPayload({ accountBattles: -1 }), "1001", "ASIA", ""),
    ).toBe("E_CONSERVATION");
  });

  it("E_CONSERVATION：wins 超过对应 battles", () => {
    const payload = validPayload() as unknown as Record<string, unknown>;
    payload.accountWins = 5001;
    expect(
      checkInvariants(payload as unknown as PlayerStatsCache, "1001", "ASIA", ""),
    ).toBe("E_CONSERVATION");
  });

  it("E_CONSERVATION：近期场次数超过总场次", () => {
    const payload = validPayload({ recent20Winrate: 0.5 }) as unknown as Record<
      string,
      unknown
    >;
    payload.recentBattles = 6000;
    expect(
      checkInvariants(payload as unknown as PlayerStatsCache, "1001", "ASIA", ""),
    ).toBe("E_CONSERVATION");
  });

  it("E_CONSERVATION：recent20Winrate 为哨兵时近期场次不受守恒约束", () => {
    const payload = validPayload({ recent20Winrate: -1 }) as unknown as Record<
      string,
      unknown
    >;
    payload.recentBattles = 6000;
    expect(
      checkInvariants(payload as unknown as PlayerStatsCache, "1001", "ASIA", ""),
    ).toBeNull();
  });

  it("E_CONSERVATION：近期 wins 超过近期场次", () => {
    const payload = validPayload() as unknown as Record<string, unknown>;
    payload.recentBattles = 100;
    payload.recentWins = 101;
    expect(
      checkInvariants(payload as unknown as PlayerStatsCache, "1001", "ASIA", ""),
    ).toBe("E_CONSERVATION");
  });

  it("E_PLAUSIBLE：场次达到百万级判定伪造", () => {
    expect(
      checkInvariants(validPayload({ accountBattles: 1_000_000 }), "1001", "ASIA", ""),
    ).toBe("E_PLAUSIBLE");
    expect(
      checkInvariants(validPayload({ accountBattles: 999_999 }), "1001", "ASIA", ""),
    ).toBeNull();
  });
});

describe("constantTimeEqual", () => {
  it("相同字符串返回 true", () => {
    expect(constantTimeEqual("abc", "abc")).toBe(true);
    expect(constantTimeEqual("", "")).toBe(true);
  });

  it("不同内容返回 false", () => {
    expect(constantTimeEqual("abc", "abd")).toBe(false);
    expect(constantTimeEqual("abc", "abd")).toBe(false);
  });

  it("长度不同返回 false", () => {
    expect(constantTimeEqual("abc", "ab")).toBe(false);
    expect(constantTimeEqual("abc", "abcd")).toBe(false);
  });

  it("前缀关系判定为不等", () => {
    expect(constantTimeEqual("abcdef", "abc")).toBe(false);
  });
});
