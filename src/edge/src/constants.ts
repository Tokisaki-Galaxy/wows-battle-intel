/**
 * 边缘后端跨 handler 共用的常量。
 *
 * 对局结果字符串同时出现在上报累加与读取侧近期窗口统计里，表名与
 * 前缀也被多处引用，统一收在这里避免两侧各写一份后漂移。
 */

/** 对局结果的稳定取值。与客户端 BattleOutcome 的存储字符串一致。 */
export const RESULT_WIN = "Win";

/** 对局结果为失败的取值。 */
export const RESULT_LOSS = "Loss";

/** 近期胜率统计的窗口，取最近若干场逐场记录。 */
export const RECENT_WINDOW = 20;

/** 统计缓存条目的生存时间，秒。 */
export const CACHE_TTL_SECONDS = 3600;

/** 统计缓存 KV 键前缀，避免与其他命名空间冲突。 */
export const STATS_CACHE_PREFIX = "stats:";

/** 模型列表查询的默认条数上限。 */
export const MODEL_LIST_LIMIT = 50;

/** 批次上报允许的最大对局数，防止单请求写放大。 */
export const MAX_BATTLES_PER_BATCH = 50;

/** 单场对局允许的最大参战者数。 */
export const MAX_PLAYERS_PER_BATTLE = 24;
