import type { ApiResponse } from "./types.js";

/**
 * 统一的 HTTP 响应构造。
 *
 * 此前 json 定义在入口文件 index.ts 里，四个 handler 反向 import 入口，
 * 形成 handler 与入口的循环依赖，响应头也在两处各写一遍。这里收口
 * 响应构造、错误信封与 CORS，handler 只关心数据本身。
 */

/** 生成 JSON 响应。cache-control 一律 no-store，数据随时间变化。 */
export function json<T>(body: ApiResponse<T>, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store",
      ...CORS_HEADERS,
    },
  });
}

/** 生成错误响应。所有失败的形如 { ok: false, error }。 */
export function fail(status: number, error: string): Response {
  return json({ ok: false, error }, status);
}

/** 读取并解析请求体 JSON。解析失败时返回 null，由调用方回 400。 */
export async function readJson(request: Request): Promise<unknown | null> {
  try {
    return await request.json();
  } catch {
    return null;
  }
}

/**
 * 浏览器跨域访问所需的响应头。
 *
 * 只开放读接口与上报所需的 GET/PUT/POST，预检缓存一天。上报不涉及
 * 凭据，因此 allow-origin 取通配。
 */
export const CORS_HEADERS: Record<string, string> = {
  "access-control-allow-origin": "*",
  "access-control-allow-methods": "GET, PUT, POST, OPTIONS",
  "access-control-allow-headers": "content-type",
  "access-control-max-age": "86400",
};

/** 预检请求的统一应答。 */
export function preflight(): Response {
  return new Response(null, { status: 204, headers: CORS_HEADERS });
}
