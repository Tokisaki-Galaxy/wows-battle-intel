/**
 * 边缘后端的绑定与环境类型。
 */

/** Cloudflare 环境绑定。与 wrangler.jsonc 中的 binding 名称一一对应。 */
export interface Env {
  /** KV：玩家统计数据缓存。读多写少，按账号 ID 键。 */
  STATS_CACHE: KVNamespace;

  /** D1：对局索引、玩家聚合、服务器基线、模型登记。 */
  DB: D1Database;

  /** R2：模型产物、训练数据导出、原始回放。 */
  MODELS: R2Bucket;

  /** 环境标识。 */
  ENVIRONMENT: string;
}

/** 统一的 API 响应信封。 */
export interface ApiResponse<T> {
  ok: boolean;
  data?: T;
  error?: string;
}

/** 对局上报条目。对应本地库的 battles 与 battle_players。 */
export interface BattleUpload {
  battleId: string;
  startedAt: number;
  mapId: string;
  battleType: string;
  server: string;
  gameVersion: string;
  result: string;
  allyScore: number;
  enemyScore: number;
  durationSec: number;
  players: PlayerUpload[];
}

/** 玩家上报条目。 */
export interface PlayerUpload {
  accountId: string;
  server: string;
  shipId: string;
  team: number;
  divisionSize: number;
  damageDealt: number;
  kills: number;
  survived: boolean;
  expEarned: number;
}

/** 批量上报请求体。 */
export interface BatchUploadRequest {
  /** 客户端生成的批次标识，用于幂等去重。 */
  batchId: string;
  /** 贡献者匿名标识。 */
  contributorHash: string;
  /** 对局列表。 */
  battles: BattleUpload[];
}

/** 玩家统计缓存条目。 */
export interface PlayerStatsCache {
  accountId: string;
  server: string;
  accountBattles: number;
  accountWinrate: number;
  recent20Winrate: number;
  updatedAt: number;
}
