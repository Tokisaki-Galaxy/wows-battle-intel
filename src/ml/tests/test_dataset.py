"""数据集构造测试。

用临时 SQLite 库验证从库中读取、特征构造、时间泄漏防护。
"""

from __future__ import annotations

import sqlite3
from pathlib import Path

import pytest

from wows_ml.dataset import RECENT_FORM_MIN_SAMPLES, load_battles

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

def add_battle(
    conn: sqlite3.Connection,
    battle_id: str,
    started_at: int,
    result: str,
    players: list[tuple[str, int]],
    battle_type: str = "Random",
) -> None:
    """插入一局与参战者。players 元素为账号与队伍的二元组。"""
    conn.execute(
        "INSERT INTO battles VALUES (?,?,?,?,?,?)",
        (battle_id, started_at, "Haven", battle_type, "Asia", result),
    )
    for _i, (account_id, team) in enumerate(players):
        conn.execute(
            "INSERT INTO battle_players (battle_id, account_id, server, player_name, "
            "ship_tier, ship_type, team, division_size, is_bot, is_hidden) "
            "VALUES (?,?,?,?,?,?,?,?,?,?)",
            (battle_id, account_id, "Asia", f"P{account_id}", 10,
             "Cruiser", team, 1, 0, 0),
        )


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


class TestRecentForm:
    """近期状态回算测试。口径见 dataset.compute_recent_form。"""

    def _fill_snapshots(self, conn: sqlite3.Connection) -> None:
        for account in ("1001", "2001"):
            conn.execute(
                "INSERT INTO player_snapshots (account_id, server, captured_at, "
                "account_battles, account_winrate, weighted_winrate) VALUES (?,?,?,?,?,?)",
                (account, "Asia", 900_000, 1000, 0.50, 0.50),
            )

    def test_recent_winrate_computed_from_local_history(self, db: str) -> None:
        """玩家有早于目标局的历史时，recent20_winrate 被回算并进聚合。"""
        conn = sqlite3.connect(db)
        self._fill_snapshots(conn)
        # 玩家 1001 的历史：5 胜 1 负，全部早于目标局 b1。
        # b1 目标局在 1_000_000。
        history = [
            ("h1", 100_000, "Win"), ("h2", 200_000, "Win"),
            ("h3", 300_000, "Loss"), ("h4", 400_000, "Win"),
            ("h5", 500_000, "Win"), ("h6", 600_000, "Win"),
        ]
        for i, (bid, ts, result) in enumerate(history):
            # 目标局与历史局都让 1001 在 team 0，result 即其胜负。
            add_battle(conn, bid, ts, result, [("1001", 0), (f"e{i}", 1)])
        conn.commit()
        conn.close()

        matches, stats = load_battles(db)

        assert len(matches) == 1
        assert stats.recent_form_computed >= 1
        # 玩家 1001 近 20 场窗口内 6 场 5 胜。
        assert matches[0].ally.avg_recent_winrate == pytest.approx(5 / 6)

    def test_recent_excludes_battles_after_target(self, db: str) -> None:
        """晚于目标局的历史不计入近期状态，避免时间泄漏。"""
        conn = sqlite3.connect(db)
        self._fill_snapshots(conn)
        # 4 场早于目标局：不足最小样本数。
        for i in range(4):
            add_battle(conn, f"h{i}", 100_000 + i * 10_000, "Win",
                       [("1001", 0), (f"e{i}", 1)])
        # 2 场晚于目标局：即使凑够样本也不得计入。
        for i in range(2):
            add_battle(conn, f"f{i}", 2_000_000 + i * 10_000, "Win",
                       [("1001", 0), ("2001", 1)])
        conn.commit()
        conn.close()

        matches, stats = load_battles(db)

        assert len(matches) == 3
        target = next(m for m in matches if m.battle_id == "b1")
        # 早于目标局的历史只有 4 场，低于最小样本数，保持哨兵。
        assert target.ally.avg_recent_winrate == 0.0
        assert stats.recent_form_insufficient >= 1

    def test_recent_respects_enemy_perspective(self, db: str) -> None:
        """敌方玩家的个人胜负按其队伍换算，与录制者视角解耦。"""
        conn = sqlite3.connect(db)
        self._fill_snapshots(conn)
        # 目标局：1001 在 team 0，2001 在 team 1，结果 Win。
        # 历史局里 2001 在 team 1（敌方），结果 Win 意味着 2001 个人判负。
        # 5 局全 Win：2001 个人 0 胜 5 负，近期胜率 0。
        for i in range(5):
            add_battle(conn, f"h{i}", 100_000 + i * 10_000, "Win",
                       [("1001", 0), ("2001", 1)])
        conn.commit()
        conn.close()

        matches, _ = load_battles(db)

        # 敌方 2001 近期胜率 0.0，非哨兵，聚合值为 0。
        assert matches[0].enemy.avg_recent_winrate == pytest.approx(0.0)

    def test_recent_ignores_coop_history(self, db: str) -> None:
        """Coop 历史不参与回算，PvP 口径与训练白名单一致。"""
        conn = sqlite3.connect(db)
        self._fill_snapshots(conn)
        # 5 场 Coop 历史不计入。
        for i in range(5):
            add_battle(conn, f"c{i}", 100_000 + i * 10_000, "Win",
                       [("1001", 0), (f"e{i}", 1)], battle_type="Coop")
        conn.commit()
        conn.close()

        matches, _ = load_battles(db)

        assert matches[0].ally.avg_recent_winrate == 0.0

    def test_recent_minimum_samples_boundary(self, db: str) -> None:
        """历史恰好达到最小样本数时产出特征，差一场则不产出。"""
        conn = sqlite3.connect(db)
        self._fill_snapshots(conn)
        for i in range(RECENT_FORM_MIN_SAMPLES):
            add_battle(conn, f"h{i}", 100_000 + i * 10_000, "Win",
                       [("1001", 0), (f"e{i}", 1)])
        conn.commit()
        conn.close()

        matches, _ = load_battles(db)

        assert matches[0].ally.avg_recent_winrate == pytest.approx(1.0)

    def test_participants_carried_for_grouping(self, db: str) -> None:
        """样本携带参战者账号，供分组交叉验证使用。"""
        conn = sqlite3.connect(db)
        self._fill_snapshots(conn)
        conn.commit()
        conn.close()

        matches, _ = load_battles(db)

        assert len(matches) == 1
        assert set(matches[0].participant_account_ids) == {"1001", "2001"}
