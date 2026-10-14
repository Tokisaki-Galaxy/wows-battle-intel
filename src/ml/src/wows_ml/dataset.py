"""从本地 SQLite 构造训练数据集。

数据流：battles + battle_players + player_snapshots -> MatchFeatures -> 训练向量。

关键约束：特征必须赛前可得。因此训练时用某局之前的玩家快照作为特征，
用该局的真实结果作为标签。回放本身只提供标签与赛后表现，不提供赛前特征。
"""

from __future__ import annotations

import sqlite3
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


def load_battles(db_path: str | Path) -> tuple[list[MatchFeatures], DatasetStats]:
    """从 SQLite 加载对局并构造特征。

    当前实现从 battle_players 还原参战者，并尝试用 player_snapshots
    补齐历史统计。若无快照，特征退化为哨兵值，此时该局对训练无价值。

    返回特征列表与统计信息。
    """
    stats = DatasetStats()
    matches: list[MatchFeatures] = []

    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        battle_rows = conn.execute(
            """
            SELECT battle_id, started_at, map_id, battle_type, server, result
            FROM battles
            ORDER BY started_at
            """
        ).fetchall()
        stats.total_battles = len(battle_rows)

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
                # 尝试用该局开始时间之前的最近快照补齐历史统计。
                snapshot = _latest_snapshot_before(
                    conn, row["account_id"], row["server"], battle["started_at"]
                )
                players.append(_build_player(row, snapshot))

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
            )

            # 若双方均无有效统计，该局对训练无价值。
            if match.ally.avg_account_winrate == 0 and match.enemy.avg_account_winrate == 0:
                stats.skipped_no_features += 1
                continue

            matches.append(match)
            stats.usable_battles += 1
    finally:
        conn.close()

    return matches, stats


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
               account_battles_div3, weighted_winrate
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

    return player


def _num(value: object, default: float) -> float:
    """把可能为 None 的数值转成 float。"""
    if value is None:
        return default
    try:
        return float(value)  # type: ignore[arg-type]
    except (TypeError, ValueError):
        return default
