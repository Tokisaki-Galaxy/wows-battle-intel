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
  const playerStats = new Map<
    string,
    { battles: number; wins: number; updatedAt: number; lastBattleAt: number }
  >();
  // 逐场记录：账号 -> 该账号参与过的所有对局结果，按比赛时间倒序排列。
  const playerResults = new Map<string, Array<{ startedAt: number; result: string }>>();

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
            return (s
              ? {
                  account_id: id,
                  server: "Asia",
                  account_battles: s.battles,
                  account_wins: s.wins,
                  updated_at: s.updatedAt,
                }
              : null) as T;
          }
          return null as T;
        },
        async all() {
          // 近期胜率查询：按账号取出逐场结果，模拟 ORDER BY started_at DESC LIMIT。
          if (sql.includes("FROM player_battle_records")) {
            const id = stmt._bindings[0] as string;
            const limit = (stmt._bindings[2] as number) ?? 20;
            const rows = (playerResults.get(id) ?? [])
              .slice()
              .sort((a, b) => b.startedAt - a.startedAt)
              .slice(0, limit)
              .map((r) => ({ result: r.result }));
            return { results: rows };
          }
          return { results: [] };
        },
        async run() {
          if (sql.includes("INSERT INTO upload_dedup")) {
            dedup.add(stmt._bindings[0] as string);
          }
          if (sql.includes("INSERT INTO battles")) {
            const battleId = stmt._bindings[0] as string;
            const startedAt = stmt._bindings[1] as number;
            const result = stmt._bindings[6] as string;
            battles.set(battleId, { startedAt, result });
          }
          if (sql.includes("INSERT INTO player_battle_records")) {
            const battleId = stmt._bindings[0] as string;
            const id = stmt._bindings[1] as string;
            const battle = battles.get(battleId) as { startedAt: number; result: string } | undefined;
            const list = playerResults.get(id) ?? [];
            list.push({ startedAt: battle?.startedAt ?? 0, result: battle?.result ?? "Unknown" });
            playerResults.set(id, list);
          }
          if (sql.includes("INSERT INTO player_stats")) {
            const id = stmt._bindings[0] as string;
            const now = stmt._bindings[2] as number;
            const won = stmt._bindings[3] as number;
            const cur = playerStats.get(id) ?? {
              battles: 0,
              wins: 0,
              updatedAt: 0,
              lastBattleAt: 0,
            };
            playerStats.set(id, {
              battles: cur.battles + 1,
              wins: cur.wins + won,
              updatedAt: now,
              lastBattleAt: now,
            });
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

  it("跨域预检返回 204 并带 CORS 头", async () => {
    const res = await worker.fetch(
      new Request("https://example.com/api/v1/stats/1001", { method: "OPTIONS" }),
      createEnv(),
      ctx,
    );
    expect(res.status).toBe(204);
    expect(res.headers.get("access-control-allow-origin")).toBe("*");
  });

  it("响应统一带 CORS 头", async () => {
    const res = await worker.fetch(new Request("https://example.com/api/health"), createEnv(), ctx);
    expect(res.headers.get("access-control-allow-origin")).toBe("*");
    expect(res.headers.get("content-type")).toContain("application/json");
  });
});

describe("上报请求校验", () => {
  function makePayload(overrides: Record<string, unknown> = {}) {
    return {
      batchId: "batch-x",
      contributorHash: "abc",
      battles: [
        {
          battleId: "bx1",
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
      ...overrides,
    };
  }

  function upload(body: unknown, env = createEnv()) {
    return worker.fetch(
      new Request("https://example.com/api/v1/battles/upload", {
        method: "POST",
        body: typeof body === "string" ? body : JSON.stringify(body),
      }),
      env,
      ctx,
    );
  }

  it("非法 JSON 返回 400 而不是 500", async () => {
    const res = await upload("{not json");
    expect(res.status).toBe(400);
  });

  it("battles 为空数组返回 400", async () => {
    const res = await upload(makePayload({ battles: [] }));
    expect(res.status).toBe(400);
  });

  it("单场对局缺少 battleId 返回 400", async () => {
    const payload = makePayload();
    payload.battles[0]!.battleId = "";
    const res = await upload(payload);
    expect(res.status).toBe(400);
  });

  it("参战者缺少 accountId 返回 400", async () => {
    const payload = makePayload();
    payload.battles[0]!.players[0]!.accountId = "";
    const res = await upload(payload);
    expect(res.status).toBe(400);
  });

  it("超出单批对局上限返回 400", async () => {
    const template = makePayload();
    const battles = Array.from({ length: 51 }, (_, i) => ({
      ...template.battles[0],
      battleId: `b${i}`,
    }));
    const res = await upload(makePayload({ battles }));
    expect(res.status).toBe(400);
  });
});

describe("统计读写口径", () => {
  it("写入越界胜率返回 400", async () => {
    const res = await worker.fetch(
      new Request("https://example.com/api/v1/stats/1001", {
        method: "PUT",
        body: JSON.stringify({ accountBattles: 10, accountWinrate: 1.7 }),
      }),
      createEnv(),
      ctx,
    );
    expect(res.status).toBe(400);
  });

  it("近期胜率按最近窗口真算，不取全生涯计数", async () => {
    const env = createEnv();
    // 前 30 场全胜，后 20 场全负，累计 50 场。
    // 全生涯胜率 0.6，最近 20 场胜率为 0，两者必须不同。
    const order = [
      ...Array.from({ length: 30 }, (_, i) => [i, true] as const),
      ...Array.from({ length: 20 }, (_, i) => [30 + i, false] as const),
    ];

    for (const [index, won] of order) {
      const payload = {
        batchId: `batch-${index}`,
        contributorHash: "abc",
        battles: [
          {
            battleId: `b-${index}`,
            startedAt: 1_700_000_000_000 + index * 60_000,
            mapId: "Haven",
            battleType: "Random",
            server: "Asia",
            gameVersion: "15.6.0",
            result: won ? "Win" : "Loss",
            allyScore: 1000,
            enemyScore: 500,
            durationSec: 900,
            players: [
              {
                accountId: "4001",
                server: "Asia",
                shipId: "PASS710",
                team: 0,
                divisionSize: 1,
                damageDealt: 1000,
                kills: 0,
                survived: true,
                expEarned: 100,
              },
            ],
          },
        ],
      };
      const res = await worker.fetch(
        new Request("https://example.com/api/v1/battles/upload", {
          method: "POST",
          body: JSON.stringify(payload),
        }),
        env,
        ctx,
      );
      expect(res.status).toBe(200);
    }

    const res = await worker.fetch(new Request("https://example.com/api/v1/stats/4001"), env, ctx);
    expect(res.status).toBe(200);
    const body = await res.json<{
      data: { accountBattles: number; accountWinrate: number; recent20Winrate: number };
    }>();
    expect(body.data.accountBattles).toBe(50);
    // 全生涯 30/50 = 0.6，最近 20 场全是败局。
    expect(body.data.accountWinrate).toBeCloseTo(0.6);
    expect(body.data.recent20Winrate).toBeCloseTo(0);
  });
});
