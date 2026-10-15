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

  /** 静态资源：下载指引页等，来自 public 目录。 */
  ASSETS: Fetcher;

  /** 环境：REPORT_SALT 参与 HMAC 与哈希派生，wrangler secret 注入。 */
  REPORT_SALT: string;

  /** 环境：管理端点 token，查询拒绝日志用。 */
  ADMIN_TOKEN: string;

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

/** 设备注册响应凭证。 */
export interface DeviceCredential {
  deviceId: string;
  /** 明文 secret 只在注册响应出现一次，服务端只存哈希，留作将来 HMAC 通信。 */
  secret: string;
  /** secret 的 HMAC(REPORT_SALT, secret) 哈希，随注册响应下发，客户端挖矿的唯一输入。 */
  secretHash: string;
  /** 上报挖矿难度：SHA256 前导零 bit 数。 */
  difficulty: number;
  /** 时隙长度，秒。 */
  slotSeconds: number;
}

/** 批量统计查询请求体。 */
export interface PlayerQueryRequest {
  deviceId: string;
  /** 查询键列表：账号级传 shipId 空串，单船级带 shipId。 */
  accounts: Array<{ accountId: string; server: string; shipId?: string }>;
}

/** 批量统计查询响应。 */
export interface PlayerQueryResponse {
  hits: Array<{ accountId: string; server: string; shipId: string; payload: PlayerStatsCache }>;
  /** 未命中清单的锁定 token，一次性，回传时必须携带。 */
  missingToken: string;
  missing: Array<{ accountId: string; server: string; shipId: string }>;
}

/** 回传请求体。 */
export interface PlayerReportRequest {
  deviceId: string;
  slot: number;
  nonce: string;
  missingToken: string;
  rows: Array<{
    accountId: string;
    server: string;
    shipId: string;
    payload: PlayerStatsCache;
  }>;
}

/** 不变量校验拒绝码。 */
export type RejectionCode =
  | "E_AUTH"
  | "E_SCOPE"
  | "E_SLOT"
  | "E_RANGE"
  | "E_CONSERVATION"
  | "E_PLAUSIBLE"
  | "E_TIME"
  | "E_FORMAT"
  | "E_LIMIT"
  | "E_TOKEN";
