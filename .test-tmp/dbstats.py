import sqlite3

c = sqlite3.connect(r"data/battles.db")
q = c.execute

for label, sql in [
    ("battles_30d", "select count(*) from battles where started_at >= datetime('now','-30 days')"),
    ("battles_90d", "select count(*) from battles where started_at >= datetime('now','-90 days')"),
    ("max_started_at", "select max(started_at) from battles"),
    ("min_started_at", "select min(started_at) from battles"),
    ("snapshots_class_missing", "select count(*) from player_snapshots where class_stats is null or class_stats = ''"),
    ("snapshots_class_present", "select count(*) from player_snapshots where class_stats is not null and class_stats != ''"),
    ("snapshots_ship_level", "select count(*) from player_snapshots where ship_id != '' and ship_winrate >= 0"),
    ("snapshots_recent_14d", "select count(*) from player_snapshots where captured_at >= datetime('now','-14 days')"),
    ("clan_grades", "select count(*) from clan_grades"),
    ("players_hidden", "select count(*) from battle_players where is_hidden = 1"),
    ("players_bot", "select count(*) from battle_players where is_bot = 1"),
    ("distinct_players", "select count(distinct account_id || server) from battle_players"),
]:
    print(label, q(sql).fetchone()[0])
