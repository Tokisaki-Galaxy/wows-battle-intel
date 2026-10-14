"""数据集构造测试。

用临时 SQLite 库验证从库中读取、特征构造、时间泄漏防护。
"""

from __future__ import annotations

import sqlite3
from pathlib import Path

import pytest

from wows_ml.dataset import load_battles

SCHEMA = """
CREATE TABLE battles (
    battle_id TEXT PRIMARY KEY,
    started_at INTEGER NOT NULL,
    map_id TEXT, battle_type TEXT, server TEXT, result TEXT
);
CREATE TABLE battle_players (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    battle_id TEXT, account_id TEXT, server TEXT, player_name TEXT,
    clan_tag TEXT, ship_id TEXT, ship_name TEXT, ship_tier INTEGER,
    ship_type TEXT, team INTEGER, division_size INTEGER,
    is_bot INTEGER, is_hidden INTEGER
);
CREATE TABLE player_snapshots (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id TEXT, server TEXT, captured_at INTEGER,
    account_battles INTEGER, account_winrate REAL,
    account_winrate_solo REAL, account_battles_solo INTEGER,
    account_winrate_div2 REAL, account_battles_div2 INTEGER,
    account_winrate_div3 REAL, account_battles_div3 INTEGER,
    weighted_winrate REAL, pr REAL, source TEXT
);
"""


@pytest.fixture
def db(tmp_path: Path) -> str:
    """构造带少量测试数据的临时库。"""
    path = tmp_path / "test.db"
    conn = sqlite3.connect(str(path))
    conn.executescript(SCHEMA)

    # 一局有结果的对局，两个玩家。
    conn.execute(
        "INSERT INTO battles VALUES (?,?,?,?,?,?)",
        ("b1", 1_000_000, "Haven", "Random", "Asia", "Win"),
    )
    conn.execute(
        "INSERT INTO battle_players (battle_id, account_id, server, player_name, ship_tier, "
        "ship_type, team, division_size, is_bot, is_hidden) VALUES (?,?,?,?,?,?,?,?,?,?)",
        ("b1", "1001", "Asia", "Ally", 10, "Battleship", 0, 1, 0, 0),
    )
    conn.execute(
        "INSERT INTO battle_players (battle_id, account_id, server, player_name, ship_tier, "
        "ship_type, team, division_size, is_bot, is_hidden) VALUES (?,?,?,?,?,?,?,?,?,?)",
        ("b1", "2001", "Asia", "Enemy", 8, "Cruiser", 1, 1, 0, 0),
    )

    # 一局无结果的对局（应被跳过）。
    conn.execute(
        "INSERT INTO battles VALUES (?,?,?,?,?,?)",
        ("b2", 2_000_000, "Haven", "Random", "Asia", "Unknown"),
    )

    conn.commit()
    conn.close()
    return str(path)


class TestLoadBattles:
    def test_skips_battles_without_result(self, db: str) -> None:
        _, stats = load_battles(db)

        assert stats.total_battles == 2
        assert stats.skipped_no_result == 1

    def test_skips_battles_without_features(self, db: str) -> None:
        # 无快照时特征全为哨兵值，对局应被跳过。
        matches, stats = load_battles(db)

        assert stats.skipped_no_features == 1
        assert len(matches) == 0

    def test_uses_snapshot_before_battle(self, db: str) -> None:
        conn = sqlite3.connect(db)
        # 对局发生在 1_000_000，插入一条更早的快照，应被采用。
        conn.execute(
            "INSERT INTO player_snapshots (account_id, server, captured_at, account_battles, "
            "account_winrate, account_winrate_solo, account_battles_solo, account_winrate_div2, "
            "account_battles_div2, account_winrate_div3, account_battles_div3, weighted_winrate) "
            "VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
            ("1001", "Asia", 900_000, 1000, 0.60, 0.60, 1000, -1, -1, -1, -1, 0.60),
        )
        conn.execute(
            "INSERT INTO player_snapshots (account_id, server, captured_at, account_battles, "
            "account_winrate, account_winrate_solo, account_battles_solo, account_winrate_div2, "
            "account_battles_div2, account_winrate_div3, account_battles_div3, weighted_winrate) "
            "VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
            ("2001", "Asia", 900_000, 1000, 0.45, 0.45, 1000, -1, -1, -1, -1, 0.45),
        )
        conn.commit()
        conn.close()

        matches, stats = load_battles(db)

        assert stats.usable_battles == 1
        assert len(matches) == 1
        match = matches[0]
        assert match.label == 1.0
        assert match.ally.avg_account_winrate == pytest.approx(0.60)
        assert match.enemy.avg_account_winrate == pytest.approx(0.45)

    def test_ignores_snapshot_after_battle(self, db: str) -> None:
        conn = sqlite3.connect(db)
        # 快照时间晚于对局，代表未来的信息，必须不被使用。
        conn.execute(
            "INSERT INTO player_snapshots (account_id, server, captured_at, account_battles, "
            "account_winrate, weighted_winrate) VALUES (?,?,?,?,?,?)",
            ("1001", "Asia", 5_000_000, 1000, 0.60, 0.60),
        )
        conn.commit()
        conn.close()

        matches, stats = load_battles(db)

        # 未来的快照被忽略，特征仍缺失。
        assert stats.skipped_no_features == 1
        assert len(matches) == 0

    def test_label_reflects_result(self, db: str) -> None:
        conn = sqlite3.connect(db)
        for account in ("1001", "2001"):
            conn.execute(
                "INSERT INTO player_snapshots (account_id, server, captured_at, account_battles, "
                "account_winrate, weighted_winrate) VALUES (?,?,?,?,?,?)",
                (account, "Asia", 900_000, 1000, 0.50, 0.50),
            )
        conn.commit()
        conn.close()

        matches, _ = load_battles(db)

        assert matches[0].label == 1.0  # 结果为 Win

    def test_skips_non_pvp_types_by_default(self, db: str) -> None:
        """Coop 等 PvE 模式默认不参与训练，避免污染样本。"""
        conn = sqlite3.connect(db)
        # 加一局有结果、有特征的 Coop 对局。若不过滤，它会被纳入。
        conn.execute(
            "INSERT INTO battles VALUES (?,?,?,?,?,?)",
            ("b3", 3_000_000, "Haven", "Coop", "Asia", "Win"),
        )
        for account, team in (("3001", 0), ("4001", 1)):
            conn.execute(
                "INSERT INTO battle_players (battle_id, account_id, server, player_name, "
                "ship_tier, ship_type, team, division_size, is_bot, is_hidden) "
                "VALUES (?,?,?,?,?,?,?,?,?,?)",
                ("b3", account, "Asia", f"P{account}", 8, "Cruiser", team, 1, 0, 0),
            )
        # 给已有随机战与新增 Coop 局都补上快照。
        for account in ("1001", "2001", "3001", "4001"):
            conn.execute(
                "INSERT INTO player_snapshots (account_id, server, captured_at, account_battles, "
                "account_winrate, weighted_winrate) VALUES (?,?,?,?,?,?)",
                (account, "Asia", 900_000, 1000, 0.50, 0.50),
            )
        conn.commit()
        conn.close()

        matches, stats = load_battles(db)

        # 总数统计全库，类型过滤单独记账。
        assert stats.total_battles == 3
        assert stats.skipped_battle_type == 1
        # 只剩那局 Random。
        assert len(matches) == 1
        assert matches[0].battle_type == "Random"

    def test_all_types_includes_coop(self, db: str) -> None:
        """显式传空白名单时不过滤，Coop 也被纳入。"""
        conn = sqlite3.connect(db)
        conn.execute(
            "INSERT INTO battles VALUES (?,?,?,?,?,?)",
            ("b3", 3_000_000, "Haven", "Coop", "Asia", "Win"),
        )
        for account, team in (("3001", 0), ("4001", 1)):
            conn.execute(
                "INSERT INTO battle_players (battle_id, account_id, server, player_name, "
                "ship_tier, ship_type, team, division_size, is_bot, is_hidden) "
                "VALUES (?,?,?,?,?,?,?,?,?,?)",
                ("b3", account, "Asia", f"P{account}", 8, "Cruiser", team, 1, 0, 0),
            )
        for account in ("1001", "2001", "3001", "4001"):
            conn.execute(
                "INSERT INTO player_snapshots (account_id, server, captured_at, account_battles, "
                "account_winrate, weighted_winrate) VALUES (?,?,?,?,?,?)",
                (account, "Asia", 900_000, 1000, 0.50, 0.50),
            )
        conn.commit()
        conn.close()

        matches, stats = load_battles(db, allowed_battle_types=())

        assert stats.skipped_battle_type == 0
        assert {m.battle_type for m in matches} == {"Random", "Coop"}
