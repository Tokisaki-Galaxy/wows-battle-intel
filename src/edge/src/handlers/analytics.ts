import type { Env } from "../types.js";
import { json } from "../index.js";

/**
 * 返回各服务器的水平分布基准。
 *
 * 客户端用这些参数把账号胜率归一化为服务器内相对位置，
 * 使跨服数据可比。见特征工程文档的服务器归一化一节。
 */
export async function handleGetBaselines(env: Env): Promise<Response> {
  const { results } = await env.DB.prepare(
    `SELECT server, computed_at, winrate_p25, winrate_p50, winrate_p75,
            winrate_mean, winrate_std, sample_size
     FROM server_baselines
     ORDER BY server`,
  ).all();

  return json({
    ok: true,
    data: {
      baselines: (results ?? []).map((row) => ({
        server: row.server,
        computedAt: row.computed_at,
        p25: row.winrate_p25,
        p50: row.winrate_p50,
        p75: row.winrate_p75,
        mean: row.winrate_mean,
        std: row.winrate_std,
        sampleSize: row.sample_size,
      })),
    },
  });
}

/**
 * 返回全局校准统计。
 *
 * 这是项目的核心公开指标，展示预测质量。多数同类工具不公布此项。
 *
 * 注意：D1 计费维度是行读取数，这里的聚合查询必须走索引。
 * 样本量增长后应改为定时预计算并写入单独的汇总表。
 */
export async function handleGetCalibration(env: Env): Promise<Response> {
  // 按对局统计规模与胜负分布。
  const totals = await env.DB.prepare(
    `SELECT
       COUNT(*) AS total_battles,
       SUM(CASE WHEN result = 'Win' THEN 1 ELSE 0 END) AS wins,
       SUM(CASE WHEN result = 'Loss' THEN 1 ELSE 0 END) AS losses,
       COUNT(DISTINCT contributor_hash) AS contributors
     FROM battles`,
  ).first<{
    total_battles: number;
    wins: number;
    losses: number;
    contributors: number;
  }>();

  const totalBattles = totals?.total_battles ?? 0;

  return json({
    ok: true,
    data: {
      totalBattles,
      wins: totals?.wins ?? 0,
      losses: totals?.losses ?? 0,
      contributors: totals?.contributors ?? 0,
      // 整体胜率仅作数据规模参考，不代表模型质量。
      // 真正的校准指标在客户端用 predictions 表本地计算。
      note: "预测校准需在客户端结合 predictions 表计算，此处仅返回采集规模。",
    },
  });
}
