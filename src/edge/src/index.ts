import type { Env } from "./types.js";
import { handleUploadBatch } from "./handlers/upload.js";
import { handleGetStats, handlePutStats } from "./handlers/stats.js";
import { handleGetModel, handleListModels } from "./handlers/models.js";
import { handleGetBaselines, handleGetCalibration } from "./handlers/analytics.js";
import { fail, json, preflight } from "./http.js";

/** 统计接口的路径前缀。 */
const STATS_PREFIX = "/api/v1/stats/";

/** 模型接口的路径前缀。 */
const MODELS_PREFIX = "/api/v1/models/";

/**
 * 中央协调库边缘后端入口。
 *
 * 职责（见 ADR 0002）：
 * - 玩家统计缓存，解决 WG API 限流
 * - 对局众包采集，积累训练数据
 * - 模型分发
 * - 服务器基线与校准统计查询
 * - 静态资源服务（下载指引页）
 *
 * 不做实时预测推理。推理在客户端本地完成（见 ADR 0003）。
 */
export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);
    const path = url.pathname;

    // 非 API 路径交给静态资源服务（下载指引页等）。
    if (!path.startsWith("/api/")) {
      return env.ASSETS.fetch(request);
    }

    // 跨域预检统一在入口应答，handler 不重复处理。
    if (request.method === "OPTIONS") {
      return preflight();
    }

    // 健康检查，不鉴权。
    if (path === "/api/health") {
      return json<{ status: string; environment: string }>({
        ok: true,
        data: { status: "healthy", environment: env.ENVIRONMENT },
      });
    }

    try {
      const response = await route(request, env, ctx, path);
      if (response) {
        return response;
      }

      return fail(404, "未找到路由");
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      console.error(`请求处理失败 ${path}: ${message}`);
      return fail(500, message);
    }
  },
} satisfies ExportedHandler<Env>;

/**
 * 路由分发。
 */
async function route(
  request: Request,
  env: Env,
  ctx: ExecutionContext,
  path: string,
): Promise<Response | null> {
  const method = request.method;

  // 对局批量上报
  if (path === "/api/v1/battles/upload" && method === "POST") {
    return handleUploadBatch(request, env, ctx);
  }

  // 玩家统计缓存
  if (path.startsWith(STATS_PREFIX)) {
    const accountId = path.slice(STATS_PREFIX.length);
    if (method === "GET") {
      return handleGetStats(accountId, env);
    }
    if (method === "PUT") {
      return handlePutStats(accountId, request, env);
    }
  }

  // 模型分发
  if (path === "/api/v1/models" && method === "GET") {
    return handleListModels(env);
  }
  if (path.startsWith(MODELS_PREFIX) && method === "GET") {
    const version = path.slice(MODELS_PREFIX.length);
    return handleGetModel(version, request, env);
  }

  // 服务器基线与校准统计
  if (path === "/api/v1/baselines" && method === "GET") {
    return handleGetBaselines(env);
  }
  if (path === "/api/v1/calibration" && method === "GET") {
    return handleGetCalibration(env);
  }

  return null;
}
