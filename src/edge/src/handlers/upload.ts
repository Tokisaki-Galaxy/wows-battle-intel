import type { BatchUploadRequest, BattleUpload, Env, PlayerUpload } from "../types.js";
import { MAX_BATTLES_PER_BATCH, MAX_PLAYERS_PER_BATTLE, RESULT_WIN } from "../constants.js";
import { fail, json, readJson } from "../http.js";

/**
 * 处理对局批量上报。
 *
 * 幂等靠 batchId 去重。入库走简单的批处理，D1 的批量语句性能足够。
 * 大流量时应迁移到 Queues 异步处理，见 ADR 0002。
 *
 * 请求体来自不可信客户端，逐字段校验后才入库：类型不符、缺少主键或
 * 超出条数上限一律 400，避免脏数据污染聚合表。
 */
export async function handleUploadBatch(
  request: Request,
  env: Env,
  _ctx: ExecutionContext,
): Promise<Response> {
  const parsed = await readJson(request);
  if (parsed === null || typeof parsed !== "object") {
    return fail(400, "请求体不是合法 JSON");
  }

  const validation = validateBatchUpload(parsed);
  if (!validation.ok) {
    return fail(400, validation.error);
  }

  const body = validation.value;

  // 幂等去重：同一批次重复上报直接返回成功。
  const existing = await env.DB.prepare(
    "SELECT batch_id FROM upload_dedup WHERE batch_id = ?1",
  )
    .bind(body.batchId)
    .first();

  if (existing) {
    return json({ ok: true, data: { batchId: body.batchId, deduplicated: true } });
  }

  const now = Date.now();
  const contributor = body.contributorHash ?? "";
  const statements: D1PreparedStatement[] = [];

  for (const battle of body.battles) {
    statements.push(
      env.DB.prepare(
        `INSERT INTO battles (
           battle_id, started_at, map_id, battle_type, server, game_version,
           result, ally_score, enemy_score, duration_sec, contributor_hash, created_at
         ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)
         ON CONFLICT(battle_id) DO NOTHING`,
      ).bind(
        battle.battleId,
        battle.startedAt,
        battle.mapId,
        battle.battleType,
        battle.server,
        battle.gameVersion,
        battle.result,
        battle.allyScore,
        battle.enemyScore,
        battle.durationSec,
        contributor,
        now,
      ),
    );

    for (const player of battle.players) {
      statements.push(buildPlayerInsert(env, battle.battleId, player, now));
      statements.push(buildPlayerStatsUpsert(env, player, battle.result, now));
    }
  }

  // 记录去重标记。
  statements.push(
    env.DB.prepare("INSERT INTO upload_dedup (batch_id, received_at) VALUES (?1, ?2)").bind(
      body.batchId,
      now,
    ),
  );

  await env.DB.batch(statements);

  return json({
    ok: true,
    data: { batchId: body.batchId, battles: body.battles.length, deduplicated: false },
  });
}

/** 校验结果。成功时携带强类型请求体，失败时携带给客户端看的原因。 */
type ValidationResult =
  | { ok: true; value: BatchUploadRequest }
  | { ok: false; error: string };

/**
 * 逐字段校验批量上报请求体。
 *
 * 手写校验而不引依赖，字段集合与 {@link BatchUploadRequest} 一一对应。
 */
function validateBatchUpload(input: object): ValidationResult {
  const raw = input as Record<string, unknown>;

  const batchId = asNonEmptyString(raw.batchId);
  if (!batchId) {
    return { ok: false, error: "缺少 batchId" };
  }

  if (!Array.isArray(raw.battles)) {
    return { ok: false, error: "battles 必须是数组" };
  }

  if (raw.battles.length === 0) {
    return { ok: false, error: "battles 不能为空" };
  }

  if (raw.battles.length > MAX_BATTLES_PER_BATCH) {
    return { ok: false, error: `单批对局数不能超过 ${MAX_BATTLES_PER_BATCH}` };
  }

  const battles: BattleUpload[] = [];
  for (const item of raw.battles) {
    const battle = validateBattle(item);
    if (!battle) {
      return { ok: false, error: "对局条目字段不完整" };
    }
    battles.push(battle);
  }

  return {
    ok: true,
    value: {
      batchId,
      contributorHash: asString(raw.contributorHash) ?? "",
      battles,
    },
  };
}

/** 校验单场对局。缺主键或参战者列表非法时返回 null。 */
function validateBattle(input: unknown): BattleUpload | null {
  if (typeof input !== "object" || input === null) {
    return null;
  }

  const raw = input as Record<string, unknown>;
  const battleId = asNonEmptyString(raw.battleId);
  const result = asNonEmptyString(raw.result);
  const startedAt = asFiniteNumber(raw.startedAt);
  // startedAt 是近期窗口的排序键，0 会被永久排到窗口末尾，必须为正。
  if (!battleId || !result || startedAt === null || startedAt <= 0) {
    return null;
  }

  // 空参战者名单不会写入逐场记录，聚合与近期窗口都会失真，直接拒绝。
  if (
    !Array.isArray(raw.players) ||
    raw.players.length === 0 ||
    raw.players.length > MAX_PLAYERS_PER_BATTLE
  ) {
    return null;
  }

  const players: PlayerUpload[] = [];
  for (const item of raw.players) {
    const player = validatePlayer(item);
    if (!player) {
      return null;
    }
    players.push(player);
  }

  return {
    battleId,
    startedAt,
    mapId: asString(raw.mapId) ?? "",
    battleType: asString(raw.battleType) ?? "Unknown",
    server: asString(raw.server) ?? "Auto",
    gameVersion: asString(raw.gameVersion) ?? "",
    result,
    allyScore: asFiniteNumber(raw.allyScore) ?? 0,
    enemyScore: asFiniteNumber(raw.enemyScore) ?? 0,
    durationSec: asFiniteNumber(raw.durationSec) ?? 0,
    players,
  };
}

/** 校验单个参战者。缺账号 ID 时返回 null。 */
function validatePlayer(input: unknown): PlayerUpload | null {
  if (typeof input !== "object" || input === null) {
    return null;
  }

  const raw = input as Record<string, unknown>;
  const accountId = asNonEmptyString(raw.accountId);
  if (!accountId) {
    return null;
  }

  return {
    accountId,
    server: asString(raw.server) ?? "Auto",
    shipId: asString(raw.shipId) ?? "",
    team: asFiniteNumber(raw.team) ?? 0,
    divisionSize: asFiniteNumber(raw.divisionSize) ?? 1,
    damageDealt: asFiniteNumber(raw.damageDealt) ?? 0,
    kills: asFiniteNumber(raw.kills) ?? 0,
    survived: raw.survived === true,
    expEarned: asFiniteNumber(raw.expEarned) ?? 0,
  };
}

/** 取非空字符串，类型不符或空串时返回 null。 */
function asNonEmptyString(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

/** 取字符串，类型不符时返回 null。 */
function asString(value: unknown): string | null {
  return typeof value === "string" ? value : null;
}

/** 取有限数字，类型不符或 NaN/Infinity 时返回 null。 */
function asFiniteNumber(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

/**
 * 构造玩家逐场记录的插入语句。
 */
function buildPlayerInsert(
  env: Env,
  battleId: string,
  player: PlayerUpload,
  now: number,
): D1PreparedStatement {
  return env.DB.prepare(
    `INSERT INTO player_battle_records (
       battle_id, account_id, server, ship_id, team, division_size,
       damage_dealt, kills, survived, exp_earned, recorded_at
     ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)`,
  ).bind(
    battleId,
    player.accountId,
    player.server,
    player.shipId,
    player.team,
    player.divisionSize,
    player.damageDealt,
    player.kills,
    player.survived ? 1 : 0,
    player.expEarned,
    now,
  );
}

/**
 * 构造玩家聚合的更新语句。
 *
 * 只累加没有时间语义的全生涯场次与胜场。近期胜率需要时间窗，由读取端
 * 按 player_battle_records 的逐场结果现算，这里不维护 recent_* 计数，
 * 否则无上限累加会退化成全生涯计数，被误读成近 20 场胜率。
 */
function buildPlayerStatsUpsert(
  env: Env,
  player: PlayerUpload,
  result: string,
  now: number,
): D1PreparedStatement {
  const won = result === RESULT_WIN ? 1 : 0;
  return env.DB.prepare(
    `INSERT INTO player_stats (
       account_id, server, updated_at, account_battles, account_wins, last_battle_at
     ) VALUES (?1, ?2, ?3, 1, ?4, ?3)
     ON CONFLICT(account_id, server) DO UPDATE SET
       updated_at      = ?3,
       account_battles = account_battles + 1,
       account_wins    = account_wins + ?4,
       last_battle_at  = ?3`,
  ).bind(player.accountId, player.server, now, won);
}
