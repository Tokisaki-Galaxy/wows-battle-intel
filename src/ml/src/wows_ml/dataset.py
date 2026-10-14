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
    # 使用了晚于对局开始时间的快照的玩家数。大于 0 表示存在时间泄漏。
    future_snapshots_used: int = 0
    # 因对局类型不在白名单而被跳过的对局数。
    skipped_battle_type: int = 0


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
               account_battles_div3, weighted_winrate, captured_at
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
