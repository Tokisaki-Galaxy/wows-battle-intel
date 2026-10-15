import { describe, it, expect } from "vitest";
import { handleDeviceRegister } from "../src/handlers/devices.js";
import { hmacHex, sha256Hex } from "../src/lib/verify.js";
import type { Env } from "../src/types.js";

/**
 * 设备注册端点测试。
 *
 * 用直接构造的 D1 mock 覆盖所需 SQL 分支：
 * devices 主键查询、条件 UPSERT 入库、rate_limits 条件累加。
 */

interface DeviceRow {
  device_id: string;
  secret_hash: string;
  created_at: number;
}

interface RateRow {
  scope: string;
  key: string;
  day: string;
  rows_used: number;
}

/**
 * 内存 D1 替身。
 *
 * 只实现 devices 注册与 rate_limits 条件累加两条路径。
 * UPSERT 的 WHERE 条件语义与 SQLite 一致：冲突时条件不满足则整条语句无效。
 */
function createMockD1() {
  const devices = new Map<string, DeviceRow>();
  const rates = new Map<string, RateRow>();

  return {
    devices,
    rates,
    db: {
      prepare(sql: string) {
        const stmt = {
          _sql: sql,
          _bindings: [] as unknown[],
          bind(...values: unknown[]) {
            stmt._bindings = values;
            return stmt;
          },
          async first<T>() {
            if (sql.includes("FROM devices")) {
              const row = devices.get(stmt._bindings[0] as string);
              return (row ? { secret_hash: row.secret_hash } : null) as T;
            }
            if (sql.includes("RETURNING rows_used")) {
              const [scope, key, day, rows, limit] = stmt._bindings as [
                string,
                string,
                string,
                number,
                number,
              ];
              const mapKey = `${scope}|${key}|${day}`;
              const cur = rates.get(mapKey);
              if (!cur) {
                // 与真实 SQLite 对齐：INSERT ... SELECT ... WHERE 限制首插分支，
                // 超限的新 key 不落行，语句无 RETURNING 结果即拒绝。
                if (rows > limit) return null;
                rates.set(mapKey, { scope, key, day, rows_used: rows });
                return { rows_used: rows } as T;
              }
              if (cur.rows_used + rows > limit) return null;
              cur.rows_used += rows;
              return { rows_used: cur.rows_used } as T;
            }
            return null as T;
          },
          async run() {
            if (sql.includes("INSERT INTO devices")) {
              const [deviceId, secretHash, now] = stmt._bindings as [string, string, number];
              devices.set(deviceId, { device_id: deviceId, secret_hash: secretHash, created_at: now });
            }
            return { success: true };
          },
          async all() {
            return { results: [] };
          },
        };
        return stmt as unknown as D1PreparedStatement;
      },
      async batch(statements: D1PreparedStatement[]) {
        for (const s of statements) {
          await (s as unknown as { run: () => Promise<unknown> }).run();
        }
        return [];
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

function registerRequest(env: Env, body: unknown): Promise<Response> {
  const db = env.DB;
  return handleDeviceRegister(
    new Request("https://example.com/api/v1/devices/register", {
      method: "POST",
      headers: { "CF-Connecting-IP": "203.0.113.7" },
      body: JSON.stringify(body),
    }),
    { ...env, DB: db },
  );
}

const SALT = "test-salt";

describe("设备注册", () => {
  it("空请求体签发新凭证，difficulty 与 slotSeconds 符合约定", async () => {
    const { db, devices } = createMockD1();
    const res = await registerRequest(createEnv(db), {});
    expect(res.status).toBe(200);

    const body = await res.json<{ ok: boolean; data: { deviceId: string; secret: string; difficulty: number; slotSeconds: number } }>();
    expect(body.ok).toBe(true);
    expect(body.data.difficulty).toBe(16);
    expect(body.data.slotSeconds).toBe(300);
    // secret 为 32 字节 hex，即 64 字符
    expect(body.data.secret).toMatch(/^[0-9a-f]{64}$/);

    // 入库只存 HMAC 哈希，明文不落库
    const row = devices.get(body.data.deviceId);
    expect(row).toBeDefined();
    const expectedHash = await hmacHex(SALT, body.data.secret);
    expect(row?.secret_hash).toBe(expectedHash);
  });

  it("携带已有凭证重注册，secret 哈希匹配时原样返回", async () => {
    const { db, devices } = createMockD1();
    const env = createEnv(db);

    // 先注册一次拿到凭证
    const first = await registerRequest(env, {});
    const cred = (await first.json<{ data: { deviceId: string; secret: string } }>()).data;
    expect(devices.size).toBe(1);

    // 幂等重放：带相同凭证再来
    const second = await registerRequest(env, {
      deviceId: cred.deviceId,
      secret: cred.secret,
    });
    expect(second.status).toBe(200);
    const body = await second.json<{ ok: boolean; data: { deviceId: string; secret: string } }>();
    expect(body.ok).toBe(true);
    expect(body.data.deviceId).toBe(cred.deviceId);
    expect(body.data.secret).toBe(cred.secret);
    expect(devices.size).toBe(1);
  });

  it("重注册 secret 错误返回 403", async () => {
    const { db, devices } = createMockD1();
    const env = createEnv(db);

    const first = await registerRequest(env, {});
    const cred = (await first.json<{ data: { deviceId: string; secret: string } }>()).data;

    const second = await registerRequest(env, {
      deviceId: cred.deviceId,
      secret: "f".repeat(64),
    });
    expect(second.status).toBe(403);
    // 库内凭证不变
    expect(devices.size).toBe(1);
  });

  it("重注册缺 secret 返回 400", async () => {
    const { db } = createMockD1();
    const res = await registerRequest(createEnv(db), { deviceId: "some-id" });
    expect(res.status).toBe(400);
  });

  it("声称有 deviceId 但库中不存在，按新设备入库并沿用该 ID", async () => {
    const { db, devices } = createMockD1();
    const res = await registerRequest(createEnv(db), {
      deviceId: "client-generated-id",
      secret: "a".repeat(64),
    });
    expect(res.status).toBe(200);
    const body = await res.json<{ data: { deviceId: string; secret: string } }>();
    expect(body.data.deviceId).toBe("client-generated-id");
    // secret 是新生成的，不等于客户端带来那份
    expect(body.data.secret).not.toBe("a".repeat(64));
    expect(devices.size).toBe(1);
  });

  it("非 JSON 请求体返回 400", async () => {
    const { db } = createMockD1();
    const res = await handleDeviceRegister(
      new Request("https://example.com/api/v1/devices/register", {
        method: "POST",
        body: "not-json",
      }),
      createEnv(db),
    );
    expect(res.status).toBe(400);
  });
});

describe("注册 IP 限流", () => {
  it("同一 IP 超过每日 50 次返回 429，计数器停在额度上", async () => {
    const { db, rates } = createMockD1();
    const env = createEnv(db);

    const ipHash = await sha256Hex(`${SALT}203.0.113.7`);
    const rateKey = `reg:${ipHash}`;

    // 前 50 次全部成功
    for (let i = 0; i < 50; i++) {
      const res = await registerRequest(env, {});
      expect(res.status).toBe(200);
    }
    const row = rates.get(`ip|${rateKey}|${new Date().toISOString().slice(0, 10)}`);
    expect(row?.rows_used).toBe(50);

    // 第 51 次被拒
    const rejected = await registerRequest(env, {});
    expect(rejected.status).toBe(429);
    expect(row?.rows_used).toBe(50);
  });

  it("不同 IP 各自独立限额", async () => {
    const { db, rates } = createMockD1();
    const env = createEnv(db);

    async function registerFrom(ip: string): Promise<Response> {
      return handleDeviceRegister(
        new Request("https://example.com/api/v1/devices/register", {
          method: "POST",
          headers: { "CF-Connecting-IP": ip },
          body: JSON.stringify({}),
        }),
        env,
      );
    }

    expect((await registerFrom("198.51.100.1")).status).toBe(200);
    expect((await registerFrom("198.51.100.2")).status).toBe(200);

    const day = new Date().toISOString().slice(0, 10);
    expect(rates.get(`ip|reg:${await sha256Hex(SALT + "198.51.100.1")}|${day}`)?.rows_used).toBe(1);
    expect(rates.get(`ip|reg:${await sha256Hex(SALT + "198.51.100.2")}|${day}`)?.rows_used).toBe(1);
  });

  it("缺少 CF-Connecting-IP 时退化为共享 key，不崩溃", async () => {
    const { db } = createMockD1();
    const res = await handleDeviceRegister(
      new Request("https://example.com/api/v1/devices/register", {
        method: "POST",
        body: JSON.stringify({}),
      }),
      createEnv(db),
    );
    expect(res.status).toBe(200);
  });
});
