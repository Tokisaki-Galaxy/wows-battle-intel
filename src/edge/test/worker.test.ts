import { describe, it, expect } from "vitest";
import worker from "../src/index.js";
import type { Env } from "../src/types.js";

/**
 * 用内存实现的最小 D1 替身。
 *
 * 只覆盖测试用到的语句，不追求完整 SQL 支持。
 */
function createMockD1(): D1Database {
  const battles = new Map<string, unknown>();
  const dedup = new Set<string>();
  const playerStats = new Map<string, { battles: number; wins: number }>();

  return {
    prepare(sql: string) {
      const stmt = {
        _sql: sql,
        _bindings: [] as unknown[],
        bind(...values: unknown[]) {
          stmt._bindings = values;
          return stmt;
        },
        async first<T>() {
          if (sql.includes("FROM upload_dedup")) {
            const id = stmt._bindings[0] as string;
            return (dedup.has(id) ? { batch_id: id } : null) as T;
          }
          if (sql.includes("FROM player_stats")) {
            const id = stmt._bindings[0] as string;
            const s = playerStats.get(id);
            return (s ? { account_id: id, server: "Asia", account_battles: s.battles, account_wins: s.wins, recent_battles: s.battles, recent_wins: s.wins, updated_at: Date.now() } : null) as T;
          }
          return null as T;
        },
        async all() {
          return { results: [] };
        },
        async run() {
          if (sql.includes("INSERT INTO upload_dedup")) {
            dedup.add(stmt._bindings[0] as string);
          }
          if (sql.includes("INSERT INTO battles")) {
            battles.set(stmt._bindings[0] as string, true);
          }
          if (sql.includes("INSERT INTO player_stats")) {
            const id = stmt._bindings[0] as string;
            const won = stmt._bindings[3] as number;
            const cur = playerStats.get(id) ?? { battles: 0, wins: 0 };
            playerStats.set(id, { battles: cur.battles + 1, wins: cur.wins + won });
          }
          return { success: true };
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
    async exec() {
      return { count: 0, duration: 0 };
    },
    async dump() {
      return new ArrayBuffer(0);
    },
  } as unknown as D1Database;
}

function createMockKV(): KVNamespace {
  const store = new Map<string, string>();
  return {
    async get(key: string, type?: string) {
      const v = store.get(key);
      if (!v) return null;
      return type === "json" ? JSON.parse(v) : v;
    },
    async put(key: string, value: string) {
      store.set(key, value);
    },
    async delete(key: string) {
      store.delete(key);
    },
    async list() {
      return { keys: [], list_complete: true, cacheStatus: null };
    },
    async getWithMetadata() {
      return { value: null, metadata: null, cacheStatus: null };
    },
  } as unknown as KVNamespace;
}

function createEnv(): Env {
  return {
    STATS_CACHE: createMockKV(),
    DB: createMockD1(),
    MODELS: {} as R2Bucket,
    ASSETS: {
      fetch: () => Promise.resolve(new Response("not found", { status: 404 })),
    } as unknown as Fetcher,
    ENVIRONMENT: "test",
  };
}

const ctx = {} as ExecutionContext;

describe("边缘后端路由", () => {
  it("健康检查返回 ok", async () => {
    const res = await worker.fetch(new Request("https://example.com/api/health"), createEnv(), ctx);
    expect(res.status).toBe(200);
    const body = await res.json<{ ok: boolean; data: { status: string } }>();
    expect(body.ok).toBe(true);
    expect(body.data.status).toBe("healthy");
  });

  it("非 API 路径交给静态资源服务", async () => {
    const env = createEnv();
    const res = await worker.fetch(new Request("https://example.com/"), env, ctx);
    expect(res.status).toBe(404);
  });

  it("未知 API 路由返回 404 JSON", async () => {
    const res = await worker.fetch(new Request("https://example.com/api/nope"), createEnv(), ctx);
    expect(res.status).toBe(404);
  });

  it("上报缺少必要字段返回 400", async () => {
    const res = await worker.fetch(
      new Request("https://example.com/api/v1/battles/upload", {
        method: "POST",
        body: JSON.stringify({}),
      }),
      createEnv(),
      ctx,
    );
    expect(res.status).toBe(400);
  });

  it("上报成功并幂等去重", async () => {
    const env = createEnv();
    const payload = {
      batchId: "batch-001",
      contributorHash: "abc",
      battles: [
        {
          battleId: "b1",
          startedAt: 1753163342000,
          mapId: "Haven",
          battleType: "Random",
          server: "Asia",
          gameVersion: "15.6.0",
          result: "Win",
          allyScore: 1000,
          enemyScore: 500,
          durationSec: 900,
          players: [
            {
              accountId: "1001",
              server: "Asia",
              shipId: "PASS710",
              team: 0,
              divisionSize: 1,
              damageDealt: 50000,
              kills: 1,
              survived: true,
              expEarned: 1500,
            },
          ],
        },
      ],
    };

    const first = await worker.fetch(
      new Request("https://example.com/api/v1/battles/upload", {
        method: "POST",
        body: JSON.stringify(payload),
      }),
      env,
      ctx,
    );
    expect(first.status).toBe(200);
    const firstBody = await first.json<{ ok: boolean; data: { deduplicated: boolean } }>();
    expect(firstBody.ok).toBe(true);
    expect(firstBody.data.deduplicated).toBe(false);

    // 重复上报同一批次应去重。
    const second = await worker.fetch(
      new Request("https://example.com/api/v1/battles/upload", {
        method: "POST",
        body: JSON.stringify(payload),
      }),
      env,
      ctx,
    );
    const secondBody = await second.json<{ data: { deduplicated: boolean } }>();
    expect(secondBody.data.deduplicated).toBe(true);
  });

  it("统计缓存未命中返回 404", async () => {
    const res = await worker.fetch(
      new Request("https://example.com/api/v1/stats/9999"),
      createEnv(),
      ctx,
    );
    expect(res.status).toBe(404);
  });

  it("写入统计后可从缓存读回", async () => {
    const env = createEnv();

    const put = await worker.fetch(
      new Request("https://example.com/api/v1/stats/1001", {
        method: "PUT",
        body: JSON.stringify({ accountBattles: 1000, accountWinrate: 0.55 }),
      }),
      env,
      ctx,
    );
    expect(put.status).toBe(200);

    const get = await worker.fetch(
      new Request("https://example.com/api/v1/stats/1001"),
      env,
      ctx,
    );
    const body = await get.json<{ ok: boolean; data: { accountWinrate: number; source: string } }>();
    expect(body.ok).toBe(true);
    expect(body.data.accountWinrate).toBeCloseTo(0.55);
    expect(body.data.source).toBe("kv");
  });

  it("写入统计缺少字段返回 400", async () => {
    const res = await worker.fetch(
      new Request("https://example.com/api/v1/stats/1001", {
        method: "PUT",
        body: JSON.stringify({ accountBattles: 1000 }),
      }),
      createEnv(),
      ctx,
    );
    expect(res.status).toBe(400);
  });

  it("模型列表返回空数组", async () => {
    const res = await worker.fetch(
      new Request("https://example.com/api/v1/models"),
      createEnv(),
      ctx,
    );
    expect(res.status).toBe(200);
    const body = await res.json<{ ok: boolean; data: { models: unknown[] } }>();
    expect(Array.isArray(body.data.models)).toBe(true);
  });

  it("服务器基线返回空数组", async () => {
    const res = await worker.fetch(
      new Request("https://example.com/api/v1/baselines"),
      createEnv(),
      ctx,
    );
    expect(res.status).toBe(200);
  });

  it("校准统计返回数据规模", async () => {
    const res = await worker.fetch(
      new Request("https://example.com/api/v1/calibration"),
      createEnv(),
      ctx,
    );
    expect(res.status).toBe(200);
    const body = await res.json<{ ok: boolean; data: { totalBattles: number } }>();
    expect(body.ok).toBe(true);
    expect(typeof body.data.totalBattles).toBe("number");
  });
});
