import type { BatchUploadRequest, Env, PlayerUpload } from "../types.js";
import { json } from "../index.js";

/**
 * 处理对局批量上报。
 *
 * 幂等靠 batchId 去重。入库走简单的批处理，D1 的批量语句性能足够。
 * 大流量时应迁移到 Queues 异步处理，见 ADR 0002。
 */
export async function handleUploadBatch(
  request: Request,
  env: Env,
  _ctx: ExecutionContext,
): Promise<Response> {
  const body = (await request.json()) as BatchUploadRequest;

  if (!body.batchId || !Array.isArray(body.battles)) {
    return json({ ok: false, error: "请求体格式不正确" }, 400);
  }

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
        battle.allyScore ?? 0,
        battle.enemyScore ?? 0,
        battle.durationSec ?? 0,
        contributor,
        now,
      ),
    );

    for (const player of battle.players ?? []) {
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
    player.divisionSize ?? 1,
    player.damageDealt ?? 0,
    player.kills ?? 0,
    player.survived ? 1 : 0,
    player.expEarned ?? 0,
    now,
  );
}

/**
 * 构造玩家长期聚合的更新语句。
 *
 * 用 UPSERT 累加场次与胜场，并维护最近窗口所需的原始计数。
 * 近期状态的计算放在读取端，聚合表只做原始累加。
 */
function buildPlayerStatsUpsert(
  env: Env,
  player: PlayerUpload,
  result: string,
  now: number,
): D1PreparedStatement {
  const won = result === "Win" ? 1 : 0;
  return env.DB.prepare(
    `INSERT INTO player_stats (
       account_id, server, updated_at, account_battles, account_wins,
       recent_battles, recent_wins, last_battle_at
     ) VALUES (?1, ?2, ?3, 1, ?4, 1, ?4, ?3)
     ON CONFLICT(account_id, server) DO UPDATE SET
       updated_at      = ?3,
       account_battles = account_battles + 1,
       account_wins    = account_wins + ?4,
       recent_battles  = recent_battles + 1,
       recent_wins     = recent_wins + ?4,
       last_battle_at  = ?3`,
  ).bind(player.accountId, player.server, now, won);
}
