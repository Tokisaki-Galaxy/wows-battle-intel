"""从本地 SQLite 构造训练数据集。

数据流：battles + battle_players + player_snapshots -> MatchFeatures -> 训练向量。

关键约束：特征必须赛前可得。因此训练时用某局之前的玩家快照作为特征，
用该局的真实结果作为标签。回放本身只提供标签与赛后表现，不提供赛前特征。
"""

from __future__ import annotations

import json
import sqlite3
from collections.abc import Iterable
from dataclasses import dataclass
from pathlib import Path

from wows_ml.features import (
    MatchFeatures,
    PlayerFeatures,
    aggregate_team,
)


@dataclass
class DatasetStats:
    """数据集构造的统计信息，用于诊断。"""

    total_battles: int = 0
    usable_battles: int = 0
    skipped_no_result: int = 0
    skipped_no_features: int = 0
    total_players: int = 0
    # 使用了晚于对局开始时间的快照的玩家数。大于 0 表示存在时间泄漏。
    future_snapshots_used: int = 0
    # 因对局类型不在白名单而被跳过的对局数。
    skipped_battle_type: int = 0
    # 近期状态回算覆盖的玩家记录数。大于 0 说明 recent20_winrate 有真实值。
    recent_form_computed: int = 0
    # 回算近期状态时，历史不足最小样本数的玩家记录数。
    recent_form_insufficient: int = 0


# 默认参与训练的 PvP 对局类型。
#
# 只保留标准玩家对抗模式。理由：
# - Coop 是 PvE，机器人占比高，胜负与玩家水平关系弱，混入是纯噪声。
# - Brawl、Event 等特殊模式机制与随机战差异大，样本量小，不宜混训。
# - Training、Operation 等非排位对抗模式本就不适合预测玩家胜负。
#
# 需要全部类型时显式传空元组。
DEFAULT_BATTLE_TYPES: tuple[str, ...] = ("Random", "Ranked")


def load_battles(
    db_path: str | Path,
    *,
    allow_future_snapshots: bool = False,
    allowed_battle_types: tuple[str, ...] = DEFAULT_BATTLE_TYPES,
) -> tuple[list[MatchFeatures], DatasetStats]:
    """从 SQLite 加载对局并构造特征。

    当前实现从 battle_players 还原参战者，并尝试用 player_snapshots
    补齐历史统计。若无快照，特征退化为哨兵值，此时该局对训练无价值。

    allow_future_snapshots：
        默认 False，只使用对局开始时间之前的快照，避免时间泄漏。
        这是正确且有生产意义的模式：客户端在开局时采集快照，
        时间戳自然早于或等于对局开始时间。

        设为 True 时，使用任意时间的最近快照来补特征。这适用于
        事后补采数据的场景，能跑通管线验证，但会产生乐观偏差，
        评估结果不可用于判断真实预测能力。使用时会记录警告。

    allowed_battle_types：
        参与训练的对局类型白名单，默认只含标准 PvP 的 Random 与 Ranked。
        Coop 等 PvE 模式的胜负与玩家水平关系弱，混入会污染训练集，
        表现为指标虚高但缺乏实际判别力。传空元组表示不过滤。

    返回特征列表与统计信息。
    """
    stats = DatasetStats()
    matches: list[MatchFeatures] = []
    future_used = 0

    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        # 全库对局总数，用于诊断，不受类型过滤影响。
        stats.total_battles = conn.execute(
            "SELECT COUNT(*) FROM battles"
        ).fetchone()[0]

        sql = """
            SELECT battle_id, started_at, map_id, battle_type, server, result
            FROM battles
            """
        params: list[str] = []
        if allowed_battle_types:
            placeholders = ", ".join("?" for _ in allowed_battle_types)
            sql += f" WHERE battle_type IN ({placeholders})"
            params = list(allowed_battle_types)
        sql += " ORDER BY started_at"

        battle_rows = conn.execute(sql, params).fetchall()
        stats.skipped_battle_type = stats.total_battles - len(battle_rows)

        for battle in battle_rows:
            if battle["result"] not in ("Win", "Loss"):
                stats.skipped_no_result += 1
                continue

            player_rows = conn.execute(
                """
                SELECT account_id, server, player_name, clan_tag, ship_id, ship_name,
                       ship_tier, ship_type, team, division_size, is_bot, is_hidden
                FROM battle_players
                WHERE battle_id = ?
                """,
                (battle["battle_id"],),
            ).fetchall()

            players: list[PlayerFeatures] = []
            for row in player_rows:
                stats.total_players += 1
                snapshot, is_future = _resolve_snapshot(
                    conn,
                    row["account_id"],
                    row["server"],
                    battle["started_at"],
                    allow_future_snapshots,
                )
                if is_future:
                    future_used += 1
                players.append(_build_player(row, snapshot))

            # 快照不携带 recent 字段，近期状态从本地对局历史回算。
            # 见 compute_recent_form 的口径注释。
            compute_recent_form(conn, players, battle["started_at"])
            stats.recent_form_computed += sum(
                1 for p in players if p.recent20_winrate >= 0
            )
            stats.recent_form_insufficient += sum(
                1 for p in players if p.recent20_winrate < 0
            )

            ally = [p for p in players if p.is_ally]
            enemy = [p for p in players if not p.is_ally]

            match = MatchFeatures(
                battle_id=battle["battle_id"],
                battle_type=battle["battle_type"],
                server=battle["server"],
                map_id=battle["map_id"] or "",
                ally=aggregate_team(ally),
                enemy=aggregate_team(enemy),
                label=1.0 if battle["result"] == "Win" else 0.0,
                participant_account_ids=tuple(
                    row["account_id"] for row in player_rows
                ),
            )

            # 若双方均无有效统计，该局对训练无价值。
            if match.ally.avg_account_winrate == 0 and match.enemy.avg_account_winrate == 0:
                stats.skipped_no_features += 1
                continue

            matches.append(match)
            stats.usable_battles += 1
    finally:
        conn.close()

    if future_used > 0:
        stats.future_snapshots_used = future_used

    return matches, stats


def _resolve_snapshot(
    conn: sqlite3.Connection,
    account_id: str,
    server: str,
    battle_started_ms: int,
    allow_future: bool,
) -> tuple[sqlite3.Row | None, bool]:
    """解析用于某玩家的快照。

    返回 (快照, 是否使用了未来快照)。
    默认只取对局开始前的最近快照，避免时间泄漏。
    """
    if not allow_future:
        return _latest_snapshot_before(conn, account_id, server, battle_started_ms), False

    # 允许未来快照时，取该玩家任意时间的最近一条。
    row = conn.execute(
        """
        SELECT account_battles, account_winrate, account_winrate_solo, account_battles_solo,
               account_winrate_div2, account_battles_div2, account_winrate_div3,
               account_battles_div3, weighted_winrate, class_stats, captured_at
        FROM player_snapshots
        WHERE account_id = ? AND server = ?
        ORDER BY captured_at DESC
        LIMIT 1
        """,
        (account_id, server),
    ).fetchone()

    if row is None:
        return None, False
    # 是否真的用到了晚于对局的快照。
    is_future = row["captured_at"] > battle_started_ms
    return row, is_future


def _latest_snapshot_before(
    conn: sqlite3.Connection,
    account_id: str,
    server: str,
    before_ms: int,
) -> sqlite3.Row | None:
    """取该时间点之前最近的一条玩家快照。

    这是避免数据泄漏的关键：只能用对局开始前已知的信息。
    """
    return conn.execute(
        """
        SELECT account_battles, account_winrate, account_winrate_solo, account_battles_solo,
               account_winrate_div2, account_battles_div2, account_winrate_div3,
               account_battles_div3, weighted_winrate, class_stats
        FROM player_snapshots
        WHERE account_id = ? AND server = ? AND captured_at <= ?
        ORDER BY captured_at DESC
        LIMIT 1
        """,
        (account_id, server, before_ms),
    ).fetchone()


def _build_player(row: sqlite3.Row, snapshot: sqlite3.Row | None) -> PlayerFeatures:
    """把数据库行转成玩家特征。"""
    from wows_ml.features import UNKNOWN

    player = PlayerFeatures(
        account_id=row["account_id"],
        server=row["server"],
        ship_tier=row["ship_tier"] or 0,
        ship_class=row["ship_type"] or "Unknown",
        division_size=row["division_size"] or 1,
        is_ally=row["team"] == 0,
        is_hidden=bool(row["is_hidden"]),
        ship_battles=UNKNOWN,
    )

    if snapshot is not None:
        player.account_battles = _num(snapshot["account_battles"], UNKNOWN)
        player.account_winrate = _num(snapshot["account_winrate"], UNKNOWN)
        player.account_winrate_solo = _num(snapshot["account_winrate_solo"], UNKNOWN)
        player.account_battles_solo = _num(snapshot["account_battles_solo"], UNKNOWN)
        player.account_winrate_div2 = _num(snapshot["account_winrate_div2"], UNKNOWN)
        player.account_battles_div2 = _num(snapshot["account_battles_div2"], UNKNOWN)
        player.account_winrate_div3 = _num(snapshot["account_winrate_div3"], UNKNOWN)
        player.account_battles_div3 = _num(snapshot["account_battles_div3"], UNKNOWN)
        player.weighted_winrate = _num(snapshot["weighted_winrate"], UNKNOWN)
        _apply_class_stats(player, snapshot["class_stats"])

    return player


# 分舰种统计 JSON 的键与采集端 ClassStatsJson 一致。
_CLASS_KEYS = ("BB", "CA", "DD", "CV", "SS")

# battle_players.ship_type 存储值到分舰种键的映射，与采集端 ToClassKey 一致。
_SHIP_TYPE_TO_KEY = {
    "Battleship": "BB",
    "Cruiser": "CA",
    "Destroyer": "DD",
    "Carrier": "CV",
    "Submarine": "SS",
}


def _apply_class_stats(player: PlayerFeatures, raw: object) -> None:
    """解析快照的分舰种 JSON，按本局舰种填充 class 字段。

    旧快照无该列或 JSON 缺本局舰种时保持 UNKNOWN，聚合层会跳过。
    JSON 损坏时静默降级为未知，不阻断数据集构造。
    """
    if not raw:
        return
    try:
        data = json.loads(raw)
    except (TypeError, ValueError):
        return

    key = _SHIP_TYPE_TO_KEY.get(player.ship_class)
    if key is None:
        return
    entry = data.get(key)
    if not isinstance(entry, dict):
        return

    battles = entry.get("battles")
    wins = entry.get("wins")
    if isinstance(battles, (int, float)) and isinstance(wins, (int, float)):
        player.class_battles = float(battles)
        player.class_wins = float(wins)


def _num(value: object, default: float) -> float:
    """把可能为 None 的数值转成 float。"""
    if value is None:
        return default
    try:
        return float(value)  # type: ignore[arg-type]
    except (TypeError, ValueError):
        return default


# 近期状态的最小历史样本数，与 C# 端 RecentFormCalculator.MinimumSamples 一致。
# 少于该数时不产出 recent 特征，保持 UNKNOWN 哨兵，由聚合层过滤。
RECENT_FORM_MIN_SAMPLES = 5

# 近期状态窗口场次，与 C# 端 RecentFormCalculator.DefaultShortWindow 一致。
RECENT_FORM_WINDOW = 20

# 参与近期状态回算的对局类型，与训练白名单同口径，只算 PvP。
RECENT_FORM_BATTLE_TYPES: tuple[str, ...] = ("Random", "Ranked")

# 近期状态回算的 SQL。参数依次为账号、目标局开始时间、类型白名单、窗口上限。
_RECENT_FORM_SQL = """
    SELECT bp.team AS team, b.result AS result
    FROM battle_players bp
    JOIN battles b ON b.battle_id = bp.battle_id
    WHERE bp.account_id = ?
      AND b.started_at < ?
      AND b.result IN ('Win', 'Loss')
      {type_clause}
    ORDER BY b.started_at DESC
    LIMIT ?
"""


def compute_recent_form(
    conn: sqlite3.Connection,
    players: Iterable[PlayerFeatures],
    battle_started_ms: int,
    *,
    window: int = RECENT_FORM_WINDOW,
    min_samples: int = RECENT_FORM_MIN_SAMPLES,
) -> None:
    """从本地对局历史回算每个玩家的近期状态，原位填充 recent 字段。

    口径与 C# 端 RecentFormCalculator 一致：玩家在目标对局开始之前，
    最近 window 场有结果 PvP 对局的胜率。对局类型过滤 Random 与 Ranked，
    只算有 Win 或 Loss 结果的局。

    时间边界用 started_at 严格早于目标局，同一秒开始的对局不计入，
    保证同批开局的样本之间互不借用信息。

    个人胜负的判定依据：battles.result 是录制者视角的结果，team 0 是
    录制者所在队伍。玩家个人胜负为 result 与其队伍的异或组合：
    录制者胜利且玩家在 team 0，或录制者失败且玩家不在 team 0，均判胜。
    依据与客户端友敌判定修复一致，见 docs/phase-data-expansion-report.md。

    战绩隐藏的玩家同样回算：隐藏只遮蔽 WG API 统计，本地逐场历史仍可见。
    无任何早于目标局历史的玩家保持 UNKNOWN，由聚合层按哨兵值过滤。
    """
    pending = [p for p in players if p.recent20_winrate < 0]
    if not pending:
        return

    type_clause = ""
    params: list[object] = []
    if RECENT_FORM_BATTLE_TYPES:
        placeholders = ", ".join("?" for _ in RECENT_FORM_BATTLE_TYPES)
        type_clause = f"AND b.battle_type IN ({placeholders})"
        params.extend(RECENT_FORM_BATTLE_TYPES)

    sql = _RECENT_FORM_SQL.format(type_clause=type_clause)
    params_window = [*params, window]

    for player in pending:
        rows = conn.execute(
            sql, [player.account_id, battle_started_ms, *params_window]
        ).fetchall()

        if len(rows) < min_samples:
            # 样本不足，保持 UNKNOWN 哨兵，聚合层会跳过。
            continue

        wins = 0
        for entry in rows:
            won = (entry["result"] == "Win") == (entry["team"] == 0)
            wins += 1 if won else 0
        player.recent20_winrate = wins / len(rows)
        player.recent20_battles = len(rows)
