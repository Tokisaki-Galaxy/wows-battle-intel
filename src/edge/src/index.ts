import type { ApiResponse, Env } from "./types.js";
import { handleUploadBatch } from "./handlers/upload.js";
import { handleGetStats, handlePutStats } from "./handlers/stats.js";
import { handleGetModel, handleListModels } from "./handlers/models.js";
import { handleGetBaselines, handleGetCalibration } from "./handlers/analytics.js";

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

      return json({ ok: false, error: "未找到路由" }, 404);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      console.error(`请求处理失败 ${path}: ${message}`);
      return json({ ok: false, error: message }, 500);
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
  if (path.startsWith("/api/v1/stats/")) {
    const accountId = path.slice("/api/v1/stats/".length);
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
  if (path.startsWith("/api/v1/models/") && method === "GET") {
    const version = path.slice("/api/v1/models/".length);
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

/**
 * 构造 JSON 响应。
 */
export function json<T>(body: ApiResponse<T>, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store",
    },
  });
}
