"""服务器基线计算。

计算各服务器的玩家水平分布，用于把绝对胜率归一化为服务器内的相对位置。

为什么需要这一步：战舰世界没有公开 MMR，玩家水平分布在不同服务器差异明显。
同样 52% 胜率，在竞争激烈的服务器与休闲服代表不同水平。绝对胜率不能跨服
直接比较，必须先转成服务器内的百分位或 z 分数，模型学到的才是相对水平。
"""

from __future__ import annotations

import sqlite3
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np


@dataclass
class ServerBaseline:
    """单个服务器的水平分布基准。"""

    server: str
    sample_size: int
    p25: float
    p50: float
    p75: float
    mean: float
    std: float


def compute_baselines(
    db_path: str | Path,
    *,
    min_samples: int = 30,
) -> list[ServerBaseline]:
    """从玩家快照计算各服务器的胜率分布。

    只统计有效样本：有账号胜率且场次达到阈值，避免把只打了几场的
    新账号计入分布，那会严重拉低分位数。

    min_samples 以下的服务器不产出基线，样本太少的分位数不可信。
    """
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    try:
        # 每个玩家取最新一条快照，避免同一玩家的历史快照重复计入。
        rows = conn.execute(
            """
            SELECT s.server, s.account_winrate, s.account_battles
            FROM player_snapshots s
            INNER JOIN (
                SELECT account_id, server, MAX(captured_at) AS latest
                FROM player_snapshots
                GROUP BY account_id, server
            ) m
              ON s.account_id = m.account_id
             AND s.server = m.server
             AND s.captured_at = m.latest
            WHERE s.account_winrate >= 0
              AND s.account_battles >= 100
            """
        ).fetchall()
    finally:
        conn.close()

    by_server: dict[str, list[float]] = {}
    for row in rows:
        by_server.setdefault(row["server"], []).append(float(row["account_winrate"]))

    baselines: list[ServerBaseline] = []
    for server, winrates in by_server.items():
        if len(winrates) < min_samples:
            continue

        arr = np.asarray(winrates, dtype=float)
        baselines.append(
            ServerBaseline(
                server=server,
                sample_size=len(arr),
                p25=float(np.percentile(arr, 25)),
                p50=float(np.percentile(arr, 50)),
                p75=float(np.percentile(arr, 75)),
                mean=float(np.mean(arr)),
                std=float(np.std(arr)),
            )
        )

    return sorted(baselines, key=lambda b: b.server)


def save_baselines(db_path: str | Path, baselines: list[ServerBaseline]) -> None:
    """把基线写入数据库。"""
    conn = sqlite3.connect(str(db_path))
    try:
        now = int(time.time() * 1000)
        for b in baselines:
            conn.execute(
                """
                INSERT INTO server_baselines (
                    server, computed_at, winrate_p25, winrate_p50, winrate_p75,
                    winrate_mean, winrate_std, sample_size
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(server) DO UPDATE SET
                    computed_at  = excluded.computed_at,
                    winrate_p25  = excluded.winrate_p25,
                    winrate_p50  = excluded.winrate_p50,
                    winrate_p75  = excluded.winrate_p75,
                    winrate_mean = excluded.winrate_mean,
                    winrate_std  = excluded.winrate_std,
                    sample_size  = excluded.sample_size
                """,
                (
                    b.server,
                    now,
                    b.p25,
                    b.p50,
                    b.p75,
                    b.mean,
                    b.std,
                    b.sample_size,
                ),
            )
        conn.commit()
    finally:
        conn.close()


def normalize_winrate(winrate: float, baseline: ServerBaseline) -> tuple[float, float]:
    """把绝对胜率转成服务器内的 z 分数与近似百分位。

    z 分数：相对该服务器均值的标准差倍数。
    百分位：用正态近似估算，不需要完整分布。
    """
    if baseline.std <= 0:
        return 0.0, 0.5

    z = (winrate - baseline.mean) / baseline.std
    # 正态累积分布近似，用误差函数。
    from math import erf, sqrt

    percentile = 0.5 * (1.0 + erf(z / sqrt(2.0)))
    return z, percentile


def describe(baselines: list[ServerBaseline]) -> str:
    """生成可读的基线摘要。"""
    if not baselines:
        return "无足够样本计算服务器基线。"

    header = (
        f"{'服务器':<8} {'样本':>8} {'25分位':>8} "
        f"{'中位':>8} {'75分位':>8} {'均值':>8} {'标准差':>8}"
    )
    lines = [
        "服务器基线（账号胜率分布）",
        "",
        header,
    ]
    for b in baselines:
        lines.append(
            f"{b.server:<8} {b.sample_size:>8} {b.p25:>8.3f} {b.p50:>8.3f} "
            f"{b.p75:>8.3f} {b.mean:>8.3f} {b.std:>8.3f}"
        )
    return "\n".join(lines)
