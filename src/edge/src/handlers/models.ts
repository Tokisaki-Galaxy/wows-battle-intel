import type { Env } from "../types.js";
import { json } from "../index.js";

/**
 * 列出可用模型版本。
 */
export async function handleListModels(env: Env): Promise<Response> {
  const { results } = await env.DB.prepare(
    `SELECT model_version, r2_key, sha256, trained_at, metrics_json, active
     FROM models
     ORDER BY active DESC, trained_at DESC
     LIMIT 50`,
  ).all();

  return json({
    ok: true,
    data: {
      models: (results ?? []).map((row) => ({
        modelVersion: row.model_version,
        r2Key: row.r2_key,
        sha256: row.sha256,
        trainedAt: row.trained_at,
        active: row.active === 1,
      })),
    },
  });
}

/**
 * 下载指定版本的模型文件。
 *
 * 模型放 R2，出网不收费。客户端走这个端点拉最新模型用于本地推理。
 */
export async function handleGetModel(
  version: string,
  _request: Request,
  env: Env,
): Promise<Response> {
  if (!version) {
    return json({ ok: false, error: "缺少模型版本" }, 400);
  }

  // 特例：请求 latest 时取当前激活版本。
  let resolvedVersion = version;
  if (version === "latest") {
    const active = await env.DB.prepare(
      "SELECT model_version FROM models WHERE active = 1 ORDER BY trained_at DESC LIMIT 1",
    ).first<{ model_version: string }>();

    if (!active) {
      return json({ ok: false, error: "没有激活的模型" }, 404);
    }
    resolvedVersion = active.model_version;
  }

  const meta = await env.DB.prepare(
    "SELECT r2_key, sha256 FROM models WHERE model_version = ?1",
  )
    .bind(resolvedVersion)
    .first<{ r2_key: string; sha256: string }>();

  if (!meta) {
    return json({ ok: false, error: "未找到该模型版本" }, 404);
  }

  const object = await env.MODELS.get(meta.r2_key);
  if (!object) {
    return json({ ok: false, error: "模型文件缺失" }, 404);
  }

  return new Response(object.body, {
    headers: {
      "content-type": "application/octet-stream",
      "content-length": String(object.size),
      // 模型内容寻址，可长期缓存。
      "cache-control": "public, max-age=31536000, immutable",
      "x-model-version": resolvedVersion,
      "x-model-sha256": meta.sha256,
    },
  });
}
