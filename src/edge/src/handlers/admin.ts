import type { Env } from "../types.js";
import { json } from "../index.js";
import { constantTimeEqual } from "../lib/verify.js";

/**
 * 拒绝日志查询端点。
 *
 * 观察面的读出口：凭 ADMIN_TOKEN 查看 rejection_log，
 * 用于线上观察攻击形态（拒绝码分布、来源设备与 IP 哈希）。
 * token 从 wrangler secret 注入，不进页面不进仓库。
 */
export async function handleAdminRejections(
  request: Request,
  env: Env,
  _ctx: ExecutionContext,
): Promise<Response> {
  const auth = request.headers.get("authorization") ?? "";
  const expected = `Bearer ${env.ADMIN_TOKEN}`;
  if (!env.ADMIN_TOKEN || !constantTimeEqual(auth, expected)) {
    return json({ ok: false, error: "未授权" }, 401);
  }

  const url = new URL(request.url);
  // code 过滤可选：?code=E_SCOPE 只看范围越界类攻击。
  const code = url.searchParams.get("code");
  // 分页参数：默认取最近 50 条。
  const limitRaw = Number(url.searchParams.get("limit") ?? "50");
  const limit = Number.isInteger(limitRaw) && limitRaw > 0 && limitRaw <= 200 ? limitRaw : 50;

  const rows = code
    ? await env.DB.prepare(
        "SELECT code, device_id, ip_hash, detail, created_at FROM rejection_log WHERE code = ?1 ORDER BY created_at DESC LIMIT ?2",
      )
      .bind(code, limit)
      .all()
    : await env.DB.prepare(
        "SELECT code, device_id, ip_hash, detail, created_at FROM rejection_log ORDER BY created_at DESC LIMIT ?1",
      )
      .bind(limit)
      .all();

  return json({ ok: true, data: { rejections: rows.results ?? [] } });
}
