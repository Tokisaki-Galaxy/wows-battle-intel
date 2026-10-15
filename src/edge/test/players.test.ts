import { describe, it, expect, beforeAll } from "vitest";
import { handleDeviceRegister } from "../src/handlers/devices.js";
import {
  handlePlayerQuery,
  handlePlayerReport,
  setRejectSampleRateForTest,
} from "../src/handlers/players.js";
import { hmacHex, sha256Hex, verifySlotTicketWithHash, currentSlot } from "../src/lib/verify.js";
import type { Env, PlayerStatsCache } from "../src/types.js";

beforeAll(() => {
  // 采样率固定为 1：拒绝日志与查询记账在测试下行为确定。
  setRejectSampleRateForTest(1);
});

/**
 * 协作缓存 v1 端点测试。
 *
 * 用内存 D1 替身覆盖所需 SQL 分支：设备主键查询、限额条件累加、
 * player_stats_cache 主键点查与条件 UPSERT、missing_tokens 生命周期、
 * report_slots 占位、rejection_log 写入。
 */

interface DeviceRow {
  device_id: string;
  secret_hash: string;
}

interface PscRow {
  payload: string;
  captured_at: number;
  device_id: string;
}

interface TokenRow {
  device_id: string;
  accounts_json: string;
  issued_at: number;
  expires_at: number;
  used_at: number | null;
}

/**
 * 内存 D1 替身。
 *
 * UPSERT 的 WHERE 语义与 SQLite 一致：冲突时条件不满足则整条语句无效。
 * psc 的条件 UPSERT 只在 excluded.captured_at 更新时覆盖。
 */
function createMockD1() {
  const devices = new Map<string, DeviceRow>();
  const rates = new Map<string, { rows_used: number }>();
  const psc = new Map<string, PscRow>();
  const tokens = new Map<string, TokenRow>();
  const slots = new Map<string, number>();
  const rejections: Array<{ code: string; device_id: string; detail: string }> = [];

  /** 执行单条语句，返回结果行数组。读与写共用这一个分派点。 */
  async function execute(sql: string, b: unknown[]): Promise<unknown[]> {
    // 写分支在前，避免 INSERT 语句体里恰好含读分支关键字时被子串匹配误判
    if (sql.includes("INSERT INTO devices")) {
      const [deviceId, secretHash] = b as [string, string];
      devices.set(deviceId, { device_id: deviceId, secret_hash: secretHash });
      return [];
    }
    if (sql.includes("FROM devices")) {
      const row = devices.get(b[0] as string);
      return row ? [{ ...row }] : [];
    }
    if (sql.includes("RETURNING rows_used")) {
      const [scope, key, d, rows, limit] = b as [string, string, string, number, number];
      const mapKey = `${scope}|${key}|${d}`;
      const cur = rates.get(mapKey);
      if (!cur) {
        // 与真实 SQLite 的 INSERT ... SELECT ... WHERE 对齐：首插同样受 limit 约束
        if (rows > limit) return [];
        rates.set(mapKey, { rows_used: rows });
        return [{ rows_used: rows }];
      }
      if (cur.rows_used + rows > limit) return [];
      cur.rows_used += rows;
      return [{ rows_used: cur.rows_used }];
    }
    if (sql.includes("FROM player_stats_cache")) {
      const [accountId, server, shipId] = b as [string, string, string];
      const row = psc.get(`${accountId}|${server}|${shipId}`);
      return row ? [{ payload: row.payload, captured_at: row.captured_at }] : [];
    }
    if (sql.includes("INSERT INTO player_stats_cache")) {
      const [accountId, server, shipId, payload, deviceId, capturedAt] = b as [
        string,
        string,
        string,
        string,
        string,
        number,
      ];
      const key = `${accountId}|${server}|${shipId}`;
      const existing = psc.get(key);
      // ON CONFLICT ... WHERE excluded.captured_at > captured_at 的语义
      if (!existing || capturedAt > existing.captured_at) {
        psc.set(key, { payload, captured_at: capturedAt, device_id: deviceId });
      }
      return [];
    }
    if (sql.includes("INSERT INTO missing_tokens")) {
      const [token, deviceId, accountsJson, issuedAt, expiresAt] = b as [
        string,
        string,
        string,
        number,
        number,
      ];
      if (!tokens.has(token)) {
        tokens.set(token, {
          device_id: deviceId,
          accounts_json: accountsJson,
          issued_at: issuedAt,
          expires_at: expiresAt,
          used_at: null,
        });
      }
      return [];
    }
    if (sql.includes("FROM missing_tokens")) {
      const row = tokens.get(b[0] as string);
      return row ? [{ ...row }] : [];
    }
    if (sql.includes("UPDATE missing_tokens")) {
      // 原子核销分支：UPDATE ... WHERE token = ?1 AND used_at IS NULL AND expires_at > ?3
      // RETURNING device_id, accounts_json。核销失败返回空，模拟真库 RETURNING 语义。
      const [token, usedAt, now] = b as [string, number, number];
      const row = tokens.get(token);
      if (row && row.used_at === null && row.expires_at > now) {
        row.used_at = usedAt;
        return [{ device_id: row.device_id, accounts_json: row.accounts_json }];
      }
      return [];
    }
    if (sql.includes("FROM report_slots")) {
      const [deviceId, slot] = b as [string, number];
      return slots.has(`${deviceId}|${slot}`) ? [{ device_id: deviceId }] : [];
    }
    if (sql.includes("INSERT INTO report_slots")) {
      const [deviceId, slot, reportedAt] = b as [string, number, number];
      const key = `${deviceId}|${slot}`;
      if (!slots.has(key)) slots.set(key, reportedAt);
      return [];
    }
    if (sql.includes("INSERT INTO rejection_log")) {
      rejections.push({
        code: b[0] as string,
        device_id: b[1] as string,
        detail: b[3] as string,
      });
      return [];
    }
    // DELETE 与未知语句一律 no-op（概率性清理不会打到任何断言）
    return [];
  }

  function makeStmt(sql: string, bindings: unknown[]) {
    return {
      _sql: sql,
      _bindings: bindings,
      bind(...values: unknown[]) {
        return makeStmt(sql, values);
      },
      async first<T>() {
        const rows = await execute(sql, bindings);
        return (rows[0] ?? null) as T;
      },
      async all<T>() {
        const rows = await execute(sql, bindings);
        return { results: rows } as T;
      },
      async run() {
        await execute(sql, bindings);
        return { success: true };
      },
    };
  }

  return {
    devices,
    rates,
    psc,
    tokens,
    slots,
    rejections,
    db: {
      prepare(sql: string) {
        return makeStmt(sql, []);
      },
      async batch(statements: Array<ReturnType<typeof makeStmt>>) {
        const out: unknown[] = [];
        for (const s of statements) {
          out.push({ results: await execute(s._sql, s._bindings) });
        }
        return out;
      },
    } as unknown as D1Database,
  };
}

function createEnv(db: D1Database): Env {
  return {
    STATS_CACHE: {} as KVNamespace,
    DB: db,
    MODELS: {} as R2Bucket,
    ASSETS: {} as Fetcher,
    REPORT_SALT: "test-salt",
    ADMIN_TOKEN: "test-admin",
    ENVIRONMENT: "test",
  };
}

/** 注册一台设备并返回完整凭证（含 secretHash）。 */
async function registerDevice(env: Env): Promise<{
  deviceId: string;
  secret: string;
  secretHash: string;
}> {
  const res = await handleDeviceRegister(
    new Request("https://example.com/api/v1/devices/register", {
      method: "POST",
      headers: { "CF-Connecting-IP": "203.0.113.7" },
      body: JSON.stringify({}),
    }),
    env,
  );
  const body = (await res.json()) as { data: { deviceId: string; secret: string; secretHash: string } };
  return body.data;
}

/** 测试用 ExecutionContext：waitUntil 只是把 promise 收着，测试内不等它。 */
const testCtx = { waitUntil: () => {} } as unknown as ExecutionContext;

/** 批量查询请求。 */
function queryRequest(env: Env, body: unknown): Promise<Response> {
  return handlePlayerQuery(
    new Request("https://example.com/api/v1/players/query", {
      method: "POST",
      headers: { "CF-Connecting-IP": "203.0.113.7" },
      body: JSON.stringify(body),
    }),
    env,
    testCtx,
  );
}

/** 回传请求。handlePlayerReport 未用 ctx，两参调用。 */
function reportRequest(env: Env, body: unknown): Promise<Response> {
  return handlePlayerReport(
    new Request("https://example.com/api/v1/players/report", {
      method: "POST",
      headers: { "CF-Connecting-IP": "203.0.113.7" },
      body: JSON.stringify(body),
    }),
    env,
  );
}

/** 暴力挖一张满足难度的时隙票据。difficulty 8 期望约 256 次尝试。 */
async function mineNonce(
  deviceId: string,
  secretHash: string,
  slot: number,
  difficulty: number,
): Promise<string> {
  for (let i = 0; i < 5_000_000; i++) {
    const nonce = i.toString(36);
    if (await verifySlotTicketWithHash(deviceId, secretHash, slot, nonce, difficulty)) {
      return nonce;
    }
  }
  throw new Error("挖矿失败，难度过高或实现有误");
}

/** 构造一份合法上报载荷。 */
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

const SALT = "test-salt";

describe("玩家统计批量查询", () => {
  it("命中与未命中分离，未命中清单签发 token 并入库", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const cred = await registerDevice(env);

    // 预置一条新鲜缓存：2001 命中，2002 未命中
    mock.psc.set("2001|ASIA|", {
      payload: JSON.stringify(validPayload({ accountId: "2001" })),
      captured_at: Date.now(),
      device_id: "other",
    });

    const res = await queryRequest(env, {
      deviceId: cred.deviceId,
      accounts: [
        { accountId: "2001", server: "ASIA" },
        { accountId: "2002", server: "ASIA" },
      ],
    });
    expect(res.status).toBe(200);
    const body = (await res.json()) as {
      data: {
        hits: Array<{ accountId: string; payload: PlayerStatsCache }>;
        missing: Array<{ accountId: string }>;
        missingToken: string;
      };
    };
    expect(body.data.hits).toHaveLength(1);
    expect(body.data.hits[0]?.accountId).toBe("2001");
    expect(body.data.hits[0]?.payload.accountWinrate).toBeCloseTo(0.52);
    expect(body.data.missing).toEqual([{ accountId: "2002", server: "ASIA", shipId: "" }]);
    expect(body.data.missingToken).toMatch(/^[0-9a-f]{32}$/);

    // token 入库：记录完整 missing 清单，30 分钟有效期
    const token = mock.tokens.get(body.data.missingToken);
    expect(token).toBeDefined();
    expect(token?.device_id).toBe(cred.deviceId);
    expect(token?.used_at).toBeNull();
    expect(token?.expires_at).toBeGreaterThan(Date.now());
    expect(JSON.parse(token?.accounts_json ?? "[]")).toEqual([
      { accountId: "2002", server: "ASIA", shipId: "" },
    ]);
  });

  it("过期条目视为 miss：账号级 7 天、单船级 3 天分层判定", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const cred = await registerDevice(env);
    const now = Date.now();
    const day = 24 * 3600 * 1000;

    mock.psc.set("3001|ASIA|", { payload: "{}", captured_at: now - 8 * day, device_id: "" });
    mock.psc.set("3002|ASIA|SHIP1", { payload: "{}", captured_at: now - 4 * day, device_id: "" });
    mock.psc.set("3003|ASIA|", { payload: "{}", captured_at: now - 6 * day, device_id: "" });
    mock.psc.set("3004|EU|S2", { payload: "{}", captured_at: now - 2 * day, device_id: "" });

    const res = await queryRequest(env, {
      deviceId: cred.deviceId,
      accounts: [
        { accountId: "3001", server: "ASIA" },
        { accountId: "3002", server: "ASIA", shipId: "SHIP1" },
        { accountId: "3003", server: "ASIA" },
        { accountId: "3004", server: "EU", shipId: "S2" },
      ],
    });
    const body = (await res.json()) as {
      data: { hits: Array<{ accountId: string }>; missing: Array<{ accountId: string }> };
    };
    // 账号级 8 天过期、单船级 4 天过期都算 miss；6 天账号级与 2 天单船级仍新鲜
    expect(body.data.hits.map((h) => h.accountId).sort()).toEqual(["3003", "3004"]);
    expect(body.data.missing.map((m) => m.accountId).sort()).toEqual(["3001", "3002"]);
  });

  it("未注册设备返回 401 并写入 rejection_log", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const res = await queryRequest(env, {
      deviceId: "ghost-device",
      accounts: [{ accountId: "1001", server: "ASIA" }],
    });
    expect(res.status).toBe(401);
    expect(mock.rejections).toHaveLength(1);
    expect(mock.rejections[0]?.code).toBe("E_AUTH");
    expect(mock.rejections[0]?.device_id).toBe("ghost-device");
  });

  it("accounts 超过 32 个返回 400 E_FORMAT", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const cred = await registerDevice(env);
    const accounts = Array.from({ length: 33 }, (_, i) => ({
      accountId: String(1000 + i),
      server: "ASIA",
    }));
    const res = await queryRequest(env, { deviceId: cred.deviceId, accounts });
    expect(res.status).toBe(400);
    expect(mock.rejections[0]?.code).toBe("E_FORMAT");
  });
});

describe("玩家统计回传", () => {
  /** 完整前置：注册设备、查询拿 token、挖难度 8 的票据。 */
  async function setupReport(
    env: Env,
    queryAccounts: Array<{ accountId: string; server: string; shipId?: string }>,
  ) {
    const cred = await registerDevice(env);
    const qRes = await queryRequest(env, { deviceId: cred.deviceId, accounts: queryAccounts });
    const qBody = (await qRes.json()) as { data: { missingToken: string } };
    const slot = currentSlot(Date.now(), 300);
    const nonce = await mineNonce(cred.deviceId, cred.secretHash, slot, 12);
    return { cred, missingToken: qBody.data.missingToken, slot, nonce };
  }

  it("全链路通过：票据验证、范围锁定、写入缓存、核销 token、占位时隙", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const { cred, missingToken, slot, nonce } = await setupReport(env, [
      { accountId: "1001", server: "ASIA" },
      { accountId: "1002", server: "EU", shipId: "PASS710" },
    ]);

    const res = await reportRequest(env, {
      deviceId: cred.deviceId,
      slot,
      nonce,
      missingToken,
      rows: [
        {
          accountId: "1001",
          server: "ASIA",
          shipId: "",
          payload: validPayload({ accountId: "1001", server: "ASIA" }),
        },
        {
          accountId: "1002",
          server: "EU",
          shipId: "PASS710",
          payload: validPayload({ accountId: "1002", server: "EU" }),
        },
      ],
    });
    expect(res.status).toBe(200);
    const body = (await res.json()) as { data: { accepted: number; slot: number } };
    expect(body.data.accepted).toBe(2);
    expect(body.data.slot).toBe(slot);

    // 缓存写入：captured_at 取服务端时间，payload 原样入库
    const now = Date.now();
    const row = mock.psc.get("1001|ASIA|");
    expect(row).toBeDefined();
    expect(row!.captured_at).toBeLessThanOrEqual(now);
    expect(row!.captured_at).toBeGreaterThan(now - 60_000);
    expect(JSON.parse(row!.payload).accountWinrate).toBeCloseTo(0.52);
    expect(mock.psc.get("1002|EU|PASS710")).toBeDefined();

    // token 一次性核销、时隙占位
    expect(mock.tokens.get(missingToken)?.used_at).not.toBeNull();
    expect(mock.slots.has(`${cred.deviceId}|${slot}`)).toBe(true);

    // 三级限额按行数累加：device / ip / global 各 2 行
    const day = new Date().toISOString().slice(0, 10);
    const ipHash = await sha256Hex(`${SALT}203.0.113.7`);
    expect(mock.rates.get(`device|${cred.deviceId}|${day}`)?.rows_used).toBe(2);
    expect(mock.rates.get(`ip|ip:${ipHash}|${day}`)?.rows_used).toBe(2);
    expect(mock.rates.get(`global|*|${day}`)?.rows_used).toBe(2);
  });

  it("注册响应下发 secretHash 且与 HMAC 口径一致，挖矿输入即 secretHash", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const cred = await registerDevice(env);
    expect(cred.secretHash).toBe(await hmacHex(SALT, cred.secret));

    // 用 secretHash 挖出的票据必须能过服务端验证；用 secret 明文挖的过不了
    const slot = currentSlot(Date.now(), 300);
    const nonce = await mineNonce(cred.deviceId, cred.secretHash, slot, 12);
    expect(
      await verifySlotTicketWithHash(cred.deviceId, cred.secretHash, slot, nonce, 8),
    ).toBe(true);
    expect(await verifySlotTicketWithHash(cred.deviceId, cred.secret, slot, nonce, 8)).toBe(false);
  });

  it("票据验证失败返回 401 E_AUTH 并写 rejection_log", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const { cred, missingToken, slot } = await setupReport(env, [
      { accountId: "1001", server: "ASIA" },
    ]);

    const res = await reportRequest(env, {
      deviceId: cred.deviceId,
      slot,
      nonce: "not-a-mined-nonce",
      missingToken,
      rows: [
        {
          accountId: "1001",
          server: "ASIA",
          shipId: "",
          payload: validPayload(),
        },
      ],
    });
    expect(res.status).toBe(401);
    expect(mock.rejections).toHaveLength(1);
    expect(mock.rejections[0]?.code).toBe("E_AUTH");
  });

  it("同时隙重复回传返回 403 E_SLOT", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const { cred, missingToken, slot, nonce } = await setupReport(env, [
      { accountId: "1001", server: "ASIA" },
    ]);
    const reqBody = {
      deviceId: cred.deviceId,
      slot,
      nonce,
      missingToken,
      rows: [{ accountId: "1001", server: "ASIA", shipId: "", payload: validPayload() }],
    };
    expect((await reportRequest(env, reqBody)).status).toBe(200);

    // 原样重放：时隙已占位，即使 token 已核销，E_SLOT 在 E_TOKEN 之前拦截
    const replay = await reportRequest(env, reqBody);
    expect(replay.status).toBe(403);
    const body = (await replay.json()) as { error: string };
    expect(body.error).toContain("E_SLOT");
    expect(mock.rejections[0]?.code).toBe("E_SLOT");
  });

  it("伪造 token 返回 403 E_TOKEN", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const { cred, slot, nonce } = await setupReport(env, [
      { accountId: "1001", server: "ASIA" },
    ]);

    const res = await reportRequest(env, {
      deviceId: cred.deviceId,
      slot,
      nonce,
      missingToken: "f".repeat(32),
      rows: [{ accountId: "1001", server: "ASIA", shipId: "", payload: validPayload() }],
    });
    expect(res.status).toBe(403);
    expect((await res.json() as { error: string }).error).toContain("E_TOKEN");
    expect(mock.rejections[0]?.code).toBe("E_TOKEN");
  });

  it("超出 token 声明范围的行返回 403 E_SCOPE", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const { cred, missingToken, slot, nonce } = await setupReport(env, [
      { accountId: "1001", server: "ASIA" },
    ]);

    const res = await reportRequest(env, {
      deviceId: cred.deviceId,
      slot,
      nonce,
      missingToken,
      // 4001 不在 token 清单内，范围锁定拦截
      rows: [{ accountId: "4001", server: "ASIA", shipId: "", payload: validPayload() }],
    });
    expect(res.status).toBe(403);
    expect(mock.rejections[0]?.code).toBe("E_SCOPE");
  });

  it("胜率 1.5 返回 400 E_RANGE", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const { cred, missingToken, slot, nonce } = await setupReport(env, [
      { accountId: "1001", server: "ASIA" },
    ]);

    const res = await reportRequest(env, {
      deviceId: cred.deviceId,
      slot,
      nonce,
      missingToken,
      rows: [
        {
          accountId: "1001",
          server: "ASIA",
          shipId: "",
          payload: validPayload({ accountWinrate: 1.5 }),
        },
      ],
    });
    expect(res.status).toBe(400);
    expect(mock.rejections[0]?.code).toBe("E_RANGE");
  });

  it("设备每日行数限额触发返回 429 E_LIMIT", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const { cred, missingToken, slot, nonce } = await setupReport(env, [
      { accountId: "1001", server: "ASIA" },
    ]);

    // 预置设备限额计数到顶：2000 行/日
    const day = new Date().toISOString().slice(0, 10);
    mock.rates.set(`device|${cred.deviceId}|${day}`, { rows_used: 2000 });

    const res = await reportRequest(env, {
      deviceId: cred.deviceId,
      slot,
      nonce,
      missingToken,
      rows: [{ accountId: "1001", server: "ASIA", shipId: "", payload: validPayload() }],
    });
    expect(res.status).toBe(429);
    expect(mock.rejections[0]?.code).toBe("E_LIMIT");
    // 限额计数不被突破
    expect(mock.rates.get(`device|${cred.deviceId}|${day}`)?.rows_used).toBe(2000);
  });

  it("captured_at 旧数据不覆盖新数据", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const cred = await registerDevice(env);
    const now = Date.now();

    // 预置一条带未来时间戳的"新"数据，再手工种一枚覆盖该键的 token
    // （模拟 token 签发后该键被更近期数据覆盖的时间线）
    mock.psc.set("1001|ASIA|", {
      payload: JSON.stringify(validPayload({ accountWinrate: 0.9 })),
      captured_at: now + 3_600_000,
      device_id: "newer-device",
    });
    mock.tokens.set("seed-token", {
      device_id: cred.deviceId,
      accounts_json: JSON.stringify([{ accountId: "1001", server: "ASIA", shipId: "" }]),
      issued_at: now,
      expires_at: now + 30 * 60_000,
      used_at: null,
    });
    const slot = currentSlot(now, 300);
    const nonce = await mineNonce(cred.deviceId, cred.secretHash, slot, 12);

    const res = await reportRequest(env, {
      deviceId: cred.deviceId,
      slot,
      nonce,
      missingToken: "seed-token",
      rows: [
        {
          accountId: "1001",
          server: "ASIA",
          shipId: "",
          payload: validPayload({ accountWinrate: 0.1 }),
        },
      ],
    });
    expect(res.status).toBe(200);

    // 旧时间戳的行被 ON CONFLICT WHERE 条件拒绝，缓存保持新数据原样
    const row = mock.psc.get("1001|ASIA|");
    expect(JSON.parse(row!.payload).accountWinrate).toBeCloseTo(0.9);
    expect(row!.captured_at).toBe(now + 3_600_000);
    expect(row!.device_id).toBe("newer-device");
  });

  it("过期 token 与已使用 token 均拒绝", async () => {
    const mock = createMockD1();
    const env = createEnv(mock.db);
    const cred = await registerDevice(env);
    const now = Date.now();

    mock.tokens.set("expired-token", {
      device_id: cred.deviceId,
      accounts_json: JSON.stringify([{ accountId: "1001", server: "ASIA", shipId: "" }]),
      issued_at: now - 60 * 60_000,
      expires_at: now - 1_000,
      used_at: null,
    });
    mock.tokens.set("used-token", {
      device_id: cred.deviceId,
      accounts_json: JSON.stringify([{ accountId: "1001", server: "ASIA", shipId: "" }]),
      issued_at: now,
      expires_at: now + 30 * 60_000,
      used_at: now - 1,
    });

    const slot = currentSlot(now, 300);
    const nonce = await mineNonce(cred.deviceId, cred.secretHash, slot, 12);
    const row = { accountId: "1001", server: "ASIA", shipId: "", payload: validPayload() };

    const expired = await reportRequest(env, {
      deviceId: cred.deviceId,
      slot,
      nonce,
      missingToken: "expired-token",
      rows: [row],
    });
    expect(expired.status).toBe(403);
    expect(mock.rejections[0]?.code).toBe("E_TOKEN");

    // 已使用 token：换上一时隙绕开时隙占位，票据也要按那一时隙重挖
    const prevSlot = slot - 1;
    const prevNonce = await mineNonce(cred.deviceId, cred.secretHash, prevSlot, 12);
    const used = await reportRequest(env, {
      deviceId: cred.deviceId,
      slot: prevSlot,
      nonce: prevNonce,
      missingToken: "used-token",
      rows: [row],
    });
    expect(used.status).toBe(403);
    expect(mock.rejections[1]?.code).toBe("E_TOKEN");
  });
});

