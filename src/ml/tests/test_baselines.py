"""服务器基线测试。

用合成快照验证分位数计算、样本过滤、归一化。
"""

from __future__ import annotations

import math
import sqlite3
from pathlib import Path

import pytest

from wows_ml.baselines import (
    ServerBaseline,
    compute_baselines,
    normalize_winrate,
    save_baselines,
)

SNAPSHOT_SCHEMA = """
CREATE TABLE player_snapshots (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id TEXT, server TEXT, captured_at INTEGER,
    account_battles INTEGER, account_winrate REAL,
    account_winrate_solo REAL, account_battles_solo INTEGER,
    account_winrate_div2 REAL, account_battles_div2 INTEGER,
    account_winrate_div3 REAL, account_battles_div3 INTEGER,
    weighted_winrate REAL, pr REAL, source TEXT
);
CREATE TABLE server_baselines (
    server TEXT PRIMARY KEY, computed_at INTEGER,
    winrate_p25 REAL, winrate_p50 REAL, winrate_p75 REAL,
    winrate_mean REAL, winrate_std REAL, sample_size INTEGER
);
"""


def _insert_snapshot(
    conn: sqlite3.Connection,
    account_id: str,
    server: str,
    winrate: float,
    battles: int,
    captured_at: int,
) -> None:
    conn.execute(
        "INSERT INTO player_snapshots (account_id, server, captured_at, account_battles, "
        "account_winrate, weighted_winrate) VALUES (?,?,?,?,?,?)",
        (account_id, server, captured_at, battles, winrate, winrate),
    )


@pytest.fixture
def db(tmp_path: Path) -> str:
    """构造带合成快照的临时库。"""
    path = tmp_path / "test.db"
    conn = sqlite3.connect(str(path))
    conn.executescript(SNAPSHOT_SCHEMA)

    # Asia 服 40 个玩家，胜率在 0.40 到 0.60 之间均匀分布。
    for i in range(40):
        winrate = 0.40 + i * 0.005
        _insert_snapshot(conn, f"asia{i}", "Asia", winrate, 1000, 1000)

    # 混入两个新账号，场次不足，应被过滤。
    _insert_snapshot(conn, "newbie1", "Asia", 0.20, 10, 1000)
    _insert_snapshot(conn, "newbie2", "Asia", 0.10, 5, 1000)

    # EU 服样本太少，不应产出基线。
    _insert_snapshot(conn, "eu1", "EU", 0.55, 1000, 1000)
    _insert_snapshot(conn, "eu2", "EU", 0.50, 1000, 1000)

    conn.commit()
    conn.close()
    return str(path)


class TestComputeBaselines:
    def test_produces_baseline_for_sufficient_server(self, db: str) -> None:
        baselines = compute_baselines(db, min_samples=30)

        assert len(baselines) == 1
        assert baselines[0].server == "Asia"
        assert baselines[0].sample_size == 40

    def test_filters_new_accounts(self, db: str) -> None:
        baselines = compute_baselines(db, min_samples=30)

        # 新账号场次不足 100，不应计入，样本数应为 40 而非 42。
        assert baselines[0].sample_size == 40

    def test_excludes_low_sample_server(self, db: str) -> None:
        baselines = compute_baselines(db, min_samples=30)

        # EU 只有 2 个样本，低于阈值。
        assert all(b.server != "EU" for b in baselines)

    def test_percentiles_are_ordered(self, db: str) -> None:
        baseline = compute_baselines(db, min_samples=30)[0]

        assert baseline.p25 <= baseline.p50 <= baseline.p75

    def test_uses_latest_snapshot_per_player(self, db: str) -> None:
        conn = sqlite3.connect(db)
        # 同一玩家插一条更新更晚的快照，应只计最新那条。
        _insert_snapshot(conn, "asia0", "Asia", 0.99, 1000, 9999)
        conn.commit()
        conn.close()

        baselines = compute_baselines(db, min_samples=30)

        # 样本数不变，说明没有把同一玩家算两次。
        assert baselines[0].sample_size == 40

    def test_no_data_returns_empty(self, tmp_path: Path) -> None:
        path = tmp_path / "empty.db"
        conn = sqlite3.connect(str(path))
        conn.executescript(SNAPSHOT_SCHEMA)
        conn.commit()
        conn.close()

        assert compute_baselines(str(path)) == []


class TestNormalizeWinrate:
    def test_at_mean_returns_zero_zscore(self) -> None:
        baseline = ServerBaseline(
            server="Asia", sample_size=100,
            p25=0.47, p50=0.50, p75=0.53, mean=0.50, std=0.05,
        )

        z, percentile = normalize_winrate(0.50, baseline)

        assert z == pytest.approx(0.0)
        assert percentile == pytest.approx(0.5)

    def test_above_mean_returns_positive_zscore(self) -> None:
        baseline = ServerBaseline(
            server="Asia", sample_size=100,
            p25=0.47, p50=0.50, p75=0.53, mean=0.50, std=0.05,
        )

        z, percentile = normalize_winrate(0.60, baseline)

        # 高出两个标准差。
        assert z == pytest.approx(2.0)
        # 百分位应接近 0.977。
        assert percentile == pytest.approx(0.977, abs=0.001)

    def test_below_mean_returns_negative_zscore(self) -> None:
        baseline = ServerBaseline(
            server="Asia", sample_size=100,
            p25=0.47, p50=0.50, p75=0.53, mean=0.50, std=0.05,
        )

        z, percentile = normalize_winrate(0.40, baseline)

        assert z == pytest.approx(-2.0)
        assert percentile == pytest.approx(0.023, abs=0.001)

    def test_zero_std_handles_gracefully(self) -> None:
        # 标准差为零的退化情形不应除零。
        baseline = ServerBaseline(
            server="Asia", sample_size=100,
            p25=0.5, p50=0.5, p75=0.5, mean=0.5, std=0.0,
        )

        z, percentile = normalize_winrate(0.6, baseline)

        assert z == 0.0
        assert percentile == 0.5

    def test_real_world_case(self) -> None:
        # 用实测的 Asia 分布：均值 0.497，标准差 0.047。
        baseline = ServerBaseline(
            server="Asia", sample_size=1600,
            p25=0.467, p50=0.493, p75=0.523, mean=0.497, std=0.047,
        )

        # 52% 胜率在这套分布下大约在前 30%。
        _, percentile = normalize_winrate(0.52, baseline)

        assert 0.6 < percentile < 0.75


class TestSaveBaselines:
    def test_saves_and_updates(self, db: str) -> None:
        baselines = compute_baselines(db, min_samples=30)
        save_baselines(db, baselines)

        conn = sqlite3.connect(db)
        conn.row_factory = sqlite3.Row
        row = conn.execute("SELECT * FROM server_baselines WHERE server = 'Asia'").fetchone()
        conn.close()

        assert row is not None
        assert row["sample_size"] == 40
        assert row["winrate_mean"] == pytest.approx(0.4975, abs=0.01)

    def test_upsert_does_not_duplicate(self, db: str) -> None:
        baselines = compute_baselines(db, min_samples=30)
        save_baselines(db, baselines)
        save_baselines(db, baselines)

        conn = sqlite3.connect(db)
        count = conn.execute("SELECT COUNT(*) FROM server_baselines").fetchone()[0]
        conn.close()

        assert count == 1


class TestMathConsistency:
    def test_percentile_matches_erf(self) -> None:
        # 验证百分位用正态近似的正确性。
        baseline = ServerBaseline(
            server="Test", sample_size=100,
            p25=0.45, p50=0.5, p75=0.55, mean=0.5, std=0.05,
        )

        _, percentile = normalize_winrate(0.55, baseline)
        # 一个标准差对应约 84.13%。
        expected = 0.5 * (1 + math.erf(1 / math.sqrt(2)))
        assert percentile == pytest.approx(expected, abs=1e-6)
