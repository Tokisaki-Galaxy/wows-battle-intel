# 数据模型

统一约定：所有时间戳用 UTC 存储。所有表带主键，写入幂等。跨库迁移时表结构尽量保持兼容。

## 本地库 SQLite

### battles

一局战斗的索引与结果。

| 字段 | 类型 | 说明 |
|---|---|---|
| battle_id | TEXT PK | 对局唯一标识，由开始时间加地图加参战者哈希生成 |
| started_at | INTEGER | 开始时间，Unix 毫秒 UTC |
| finished_at | INTEGER | 结束时间，Unix 毫秒 UTC，解析回放后填充 |
| map_id | TEXT | 地图标识 |
| battle_type | TEXT | 对局类型，如 Random、Ranked、Coop |
| game_version | TEXT | 游戏版本，用于回放解析匹配 |
| server | TEXT | 服务器，RU/EU/NA/ASIA/CN |
| result | TEXT | 结果，WIN/LOSS/DRAW/UNKNOWN |
| ally_score | INTEGER | 友方得分 |
| enemy_score | INTEGER | 敌方得分 |
| duration_sec | INTEGER | 对局时长秒 |
| replay_path | TEXT | 本地回放文件路径，可为空 |
| replay_parsed | INTEGER | 是否已解析回放，0/1 |
| created_at | INTEGER | 记录创建时间 |

索引：`started_at`、`battle_type`、`server`。

### battle_players

一局中每个参战者的表现。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | INTEGER PK AUTOINCREMENT | 自增主键 |
| battle_id | TEXT FK | 关联 battles |
| account_id | TEXT | 账号 ID，跨服唯一性以 server 加 account_id 为准 |
| server | TEXT | 服务器 |
| player_name | TEXT | 对局时的昵称快照 |
| clan_tag | TEXT | 公会标签快照 |
| ship_id | TEXT | 船 ID |
| ship_tier | INTEGER | 船等级 |
| ship_type | TEXT | 舰种，DD/CA/BB/CV/SS |
| team | INTEGER | 阵营，0 友方 1 敌方 |
| division_size | INTEGER | 组队规模，1/2/3 |
| is_bot | INTEGER | 是否机器人 |
| is_hidden | INTEGER | 是否隐藏战绩 |
| damage_dealt | INTEGER | 造成伤害 |
| damage_taken | INTEGER | 承受伤害 |
| kills | INTEGER | 击杀数 |
| spotting_damage | INTEGER | 点亮伤害 |
| potential_damage | INTEGER | 潜在伤害 |
| survived | INTEGER | 是否存活 |
| survived_sec | INTEGER | 存活秒数 |
| exp_earned | INTEGER | 获得经验 |
| raw_json | TEXT | 回放解析的原始记录，便于回溯 |

唯一约束：`(battle_id, account_id, server)`。

索引：`account_id`、`(account_id, battle_id)`、`battle_id`。

### predictions

赛前预测记录，用于事后校准。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | INTEGER PK AUTOINCREMENT | 自增主键 |
| battle_id | TEXT | 关联 battles，赛前可能尚无对应行 |
| predicted_at | INTEGER | 预测时间，Unix 毫秒 UTC |
| model_version | TEXT | 模型版本标识 |
| model_hash | TEXT | 模型文件哈希，确保可追溯 |
| ally_win_prob | REAL | 友方获胜概率，0 到 1 |
| confidence | REAL | 模型置信度，0 到 1 |
| feature_json | TEXT | 预测时的特征快照，用于复盘与再训练 |
| features_available | INTEGER | 特征完整度标记，缺失特征时降级 |
| actual_result | TEXT | 事后回填的真实结果 |
| calibrated | INTEGER | 是否已参与校准统计，0/1 |
| created_at | INTEGER | 记录创建时间 |

索引：`battle_id`、`predicted_at`、`(calibrated, actual_result)`。

设计要点：`feature_json` 快照是刻意的冗余。它让预测在事后完全可复现，也让你能在模型迭代后回放历史特征重新评估，不必重跑整条采集链路。这是校准可信度的基础。

### player_snapshots

玩家统计的时间序列快照，近期状态特征的原料。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | INTEGER PK AUTOINCREMENT | 自增主键 |
| account_id | TEXT | 账号 ID |
| server | TEXT | 服务器 |
| captured_at | INTEGER | 抓取时间，Unix 毫秒 UTC |
| account_battles | INTEGER | 账号总场次 |
| account_winrate | REAL | 账号总胜率 |
| account_winrate_solo | REAL | 单人胜率 |
| account_battles_solo | INTEGER | 单人场次 |
| account_winrate_div2 | REAL | 双排胜率 |
| account_battles_div2 | INTEGER | 双排场次 |
| account_winrate_div3 | REAL | 三排胜率 |
| account_battles_div3 | INTEGER | 三排场次 |
| weighted_winrate | REAL | 加权胜率 |
| pr | REAL | 个人评分，可空 |
| source | TEXT | 数据来源，VORTEX/WG_PUBLIC/YUYUKO |
| class_stats | TEXT | 分舰种聚合统计 JSON，形如 `{"BB":{"battles":1000,"wins":560,"damage":50000}}` |
| clan_tag | TEXT | 采集时的公会标签快照，含方括号。身份字段，不参与训练，供缓存加速路径回填名单 |
| ship_id | TEXT | 快照对应本局船只的标识，空表示纯账号级快照 |
| ship_battles | INTEGER | 本局船只的账号场次 |
| ship_winrate | REAL | 本局船只的账号胜率 |
| ship_avg_damage | REAL | 本局船只的账号场均伤害 |
| favorite_tier | INTEGER | 账号最常玩等级 |

唯一约束：`(account_id, server, captured_at)`。

索引：`(account_id, server, captured_at)`。

这张表是逐场累积的核心。有了它才能算出"最近 20 场胜率"这类短期指标，WG 公开 API 本身不提供逐场历史。

## 云端库

云端起步用 Cloudflare D1，表结构与本地对应但做裁剪，明细往 R2 推。

### battles 云端

只保留可索引的查询字段，明细压缩或进 R2。

| 字段 | 类型 | 说明 |
|---|---|---|
| battle_id | TEXT PK | 对局标识 |
| started_at | INTEGER | 开始时间 |
| map_id | TEXT | 地图 |
| battle_type | TEXT | 对局类型 |
| server | TEXT | 服务器 |
| game_version | TEXT | 游戏版本 |
| result | TEXT | 结果 |
| contributor_hash | TEXT | 贡献者匿名标识，用于去重与统计 |

### player_battle_records 云端

逐场玩家记录，只增不减，是数据增长的主要来源。设计上要能整体迁出到 R2 或外部库。

| 字段 | 类型 | 说明 |
|---|---|---|
| id | INTEGER PK AUTOINCREMENT | 自增主键 |
| battle_id | TEXT | 对局标识 |
| account_id | TEXT | 账号 ID |
| server | TEXT | 服务器 |
| ship_id | TEXT | 船 ID |
| team | INTEGER | 阵营 |
| damage_dealt | INTEGER | 造成伤害 |
| kills | INTEGER | 击杀 |
| survived | INTEGER | 是否存活 |
| exp_earned | INTEGER | 经验 |

索引：`(account_id, battle_id)`、`battle_id`。分区或分表按时间切，控制单表规模。

### player_stats_cache 云端

玩家统计缓存，与 KV 配合。KV 做热缓存，这张表做冷备份与聚合。

### server_baselines

各服务器的水平分布基准，用于胜率归一化。

| 字段 | 类型 | 说明 |
|---|---|---|
| server | TEXT PK | 服务器 |
| computed_at | INTEGER | 计算时间 |
| winrate_p25 | REAL | 胜率 25 分位 |
| winrate_p50 | REAL | 胜率中位数 |
| winrate_p75 | REAL | 胜率 75 分位 |
| winrate_mean | REAL | 胜率均值 |
| winrate_std | REAL | 胜率标准差 |
| sample_size | INTEGER | 样本量 |

### models

模型版本登记。

| 字段 | 类型 | 说明 |
|---|---|---|
| model_version | TEXT PK | 版本标识 |
| r2_key | TEXT | R2 中的对象键 |
| sha256 | TEXT | 文件哈希 |
| trained_at | INTEGER | 训练时间 |
| metrics_json | TEXT | 训练指标，含 Brier 分数与校准信息 |
| active | INTEGER | 是否为当前下发版本 |

## 容量规划

关键约束：D1 单库 10GB 上限，计费维度是行读取数。因此：

- 云端 `battles` 表单行保持小，只留索引字段。
- 逐场明细 `player_battle_records` 是增长主力，预留迁出路径，规模到阈值时迁到 R2 或外部 Postgres。
- 所有查询必须走索引，禁止全表扫描。
- 聚合结果预先算好单独存，避免对明细做反复扫描。

数据量估算：一局 24 人，`player_battle_records` 一行约 100 字节。一百万局约 2400 万行，约 2.4GB。加上索引与开销，D1 大约能撑几十万局。到那个量级就该考虑迁出，`IBattleRepository` 抽象让迁移只是加一个实现。
