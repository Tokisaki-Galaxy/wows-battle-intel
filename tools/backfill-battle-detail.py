"""把逐船明细、成就与组队标识三个派生字段一次性回填到库里。

三个字段都来自同一个回放解析产物，分开补容易出现「明细有了、组队没补」
这种半截状态，所以合并成一个脚本，重复执行安全。

- damage_by_target：逐船伤害明细，含目标船数字 ID
- achievements：成就展示名
- division_id：组队标识，同一小队成员共享

用法：python tools/backfill-battle-detail.py
"""

import json
import os
import sqlite3
import subprocess
import sys

ROOT = r"Z:\Code\GitHub\wows-battle-intel"
DB = os.path.join(ROOT, "data", "battles.db")
GAME = r"D:\SteamLibrary\steamapps\common\World of Warships"
REPLAYSHARK = os.path.join(ROOT, "data", "tools", "replayshark.exe")
OUT_DIR = os.path.join(ROOT, "data", "parsed", "migrated")


def add_missing_columns(conn):
    """给旧库补列，与客户端启动时的迁移保持一致。"""
    expected = {
        "division_id": "TEXT",
        "damage_by_target": "TEXT",
        "achievements": "TEXT",
    }
    existing = {row[1] for row in conn.execute("PRAGMA table_info(battle_players);")}
    for column, sql_type in expected.items():
        if column in existing:
            continue
        conn.execute(f"ALTER TABLE battle_players ADD COLUMN {column} {sql_type};")
        print(f"补列 {column}")


def map_achievements(player):
    """成就展示文本。数量大于 1 时写成「名称 ×N」，中文转换在显示层做。"""
    out = []
    for item in player.get("achievements") or []:
        if not item:
            continue
        label = (item.get("display_name") or item.get("name") or "").strip()
        if not label:
            continue
        count = item.get("count") or 1
        out.append(f"{label} ×{count}" if count > 1 else label)
    return out


def map_damage(player, by_id):
    """逐船伤害明细，按我对目标造成的伤害降序。"""
    interactions = (player.get("server_results") or {}).get("damage_interactions") or {}
    result = []
    for target_id, value in interactions.items():
        if not value:
            continue
        dealt = value.get("damage_dealt") or 0
        received = value.get("damage_received") or 0
        if dealt == 0 and received == 0:
            continue
        target = by_id.get(str(target_id)) or {}
        result.append({
            "targetPlayerId": str(target_id),
            "targetShipId": str(target.get("ship_id") or ""),
            "targetShipName": target.get("ship_name") or "",
            "targetShipTier": target.get("ship_tier") or 0,
            "targetShipClass": target.get("ship_class") or "Unknown",
            "damageDealt": dealt,
            "damageReceived": received,
        })
    result.sort(key=lambda d: (-d["damageDealt"], d["targetShipName"]))
    return result


def load_result(battle_id, path):
    """读取解析产物，遇到原始回放先解析一次。"""
    if path.lower().endswith(".wowsreplay"):
        os.makedirs(OUT_DIR, exist_ok=True)
        out = os.path.join(OUT_DIR, f"{battle_id}.json")
        if not os.path.exists(out):
            subprocess.run(
                [REPLAYSHARK, "-g", GAME, "battle-results", "--format", "normalized",
                 "--out-file", out, path],
                capture_output=True)
        if not os.path.exists(out):
            return None, path
        path = out

    try:
        data = json.load(open(path, encoding="utf-8"))
    except (OSError, ValueError):
        return None, path

    item = data[0] if isinstance(data, list) else data
    return item, path


def main():
    conn = sqlite3.connect(DB)
    add_missing_columns(conn)
    conn.commit()

    rows = conn.execute(
        "select battle_id, replay_path from battles where replay_path is not null and replay_path <> ''"
    ).fetchall()
    print(f"待处理对局 {len(rows)}")

    battles_done = 0
    players_done = 0
    division_done = 0
    skipped = 0

    for index, (battle_id, replay_path) in enumerate(rows, 1):
        path = replay_path if os.path.isabs(replay_path) else os.path.join(ROOT, replay_path)
        if not os.path.exists(path):
            skipped += 1
            continue

        item, resolved = load_result(battle_id, path)
        if item is None:
            skipped += 1
            continue

        players = item.get("players") or []
        by_id = {str(p.get("db_id")): p for p in players}
        existing = {}
        for rowid, name in conn.execute(
                "select rowid, player_name from battle_players where battle_id = ?", (battle_id,)):
            existing.setdefault(name, []).append(rowid)

        wrote = 0
        for player in players:
            rowids = existing.get(player.get("name") or "")
            if not rowids:
                continue

            details = map_damage(player, by_id)
            achievements = map_achievements(player)
            division = player.get("division_id")
            division_id = str(division) if division else None
            if division_id:
                division_done += 1

            for rowid in rowids:
                conn.execute(
                    "update battle_players set damage_by_target = ?, achievements = ?, division_id = ? where rowid = ?",
                    (json.dumps(details, ensure_ascii=False) if details else None,
                     json.dumps(achievements, ensure_ascii=False) if achievements else None,
                     division_id,
                     rowid))
                wrote += 1

        if resolved != replay_path:
            conn.execute("update battles set replay_path = ? where battle_id = ?", (resolved, battle_id))

        if wrote:
            battles_done += 1
            players_done += wrote

        if index % 100 == 0:
            conn.commit()
            print(f"进度 {index}/{len(rows)} · 对局 {battles_done} · 玩家行 {players_done}", flush=True)

    conn.commit()

    with_detail = conn.execute(
        "select count(*) from battle_players where damage_by_target is not null").fetchone()[0]
    with_division = conn.execute(
        "select count(*) from battle_players where division_id is not null").fetchone()[0]
    missing = conn.execute(
        "select count(*) from battle_players where damage_by_target is null and damage_dealt > 0"
    ).fetchone()[0]
    conn.close()

    print(f"完成 · 对局 {battles_done} · 玩家行 {players_done} · 跳过 {skipped}")
    print(f"有明细的行 {with_detail} · 有组队标识的行 {with_division} · 缺明细的行 {missing}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
