# 分舰种熟练度特征设计（features-v2）

用户核心诉求：模型要能区分"60% 胜率的大佬开 CV"与"60% 大佬开后排 BB"这两类
局面，并识别"本局他在不熟悉的船上"这类情况。当前 20 维特征全是账号级均值，
单船信号只通过 WeightedWinrate 以不超过 20% 的权重混入，白板船惩罚被稀释；
队伍聚合也丢失了舰种对位信息。

本文档定义新增特征集合、公式与收缩口径。改动必须两端同步：Python 端
`MatchFeatures.feature_names()` 与 C# 端 `FeatureVectorBuilder.FeatureNames`，
顺序契约由单测强制校验。

## 设计原则

1. 赛前可得。只用开局前可获取的统计，快照按 `captured_at <= started_at` 选取。
2. 经验贝叶斯收缩。分舰种与单船胜率必须收缩，防止小样本噪声。
3. 未知保持哨兵。旧数据无分舰种字段，聚合层按 UNKNOWN 过滤，不伪装成 0.5。
4. 差分优先。对位差分比绝对值更有判别力，与现有 20 维的口径一致。

## 收缩公式与口径

对任意玩家在舰种 c 上的统计：

    class_wr_shrunk = (wins_c + m * prior) / (battles_c + m)

- m = 40。先验等效场次，落在建议区间 30-50 的中部。舰种级样本比单船更少
  噪声更大，取偏大值更稳。
- prior = 账号自身胜率 account_winrate，缺失时取 0.5。不用库内舰种全体均值
  做先验，理由：先验必须训练端与推理端完全一致，账号胜率在两侧都赛前可得，
  库内均值会随时间漂移并引入跨端依赖。未玩过该舰种时结果趋近账号自身水平，
  配合 battles_c = 0 让模型识别这种状态。

单船收缩同式，m 同为 40，prior 同为账号自身胜率。

## 新增特征清单

### a. 玩家级（入 PlayerFeatures / PlayerRecord）

| 特征 | 公式 | 说明 |
|---|---|---|
| class_wr_shrunk | 见上式 | 本局所开舰种的收缩胜率 |
| class_battles | battles_c | 该舰种总场次 |
| class_specialty | class_wr_shrunk - account_winrate | 舰种专精度，正值为该舰种比账号整体强 |
| ship_wr_shrunk | (wins_ship + m * account_wr) / (battles_ship + m) | 单船收缩胜率，替代 20% 线性混成的信号源 |
| ship_battles | 已有 | 该船场次，旧数据本就可采 |
| ship_is_fresh | 1 if ship_battles < 10 else 0 | 白板船标记，battles 未知时为 UNKNOWN |

### b. 队伍聚合与对位差分（入 TeamFeatures / to_vector）

| 特征 | 公式 | 说明 |
|---|---|快照携带 |
| ally_class_wr / enemy_class_wr | mean(class_wr_shrunk) | 各队按本局舰种的收缩胜率均值 |
| class_wr_diff | ally - enemy | 主对位差分 |
| ally_dd_wr / enemy_dd_wr | mean over DD players | DD 收缩胜率均值 |
| dd_wr_diff | ally DD - enemy DD | DD 对位差分，DD 有战略杠杆 |
| dd_count_diff | ally DD 数 - enemy DD 数 | 依赖 battle_players.ship_type，存量数据立即可用 |
| cv_count_diff | 同上 | 同上 |
| ally_cv_wr / enemy_cv_wr | mean over CV players | CV 对位差分 |
| cv_wr_diff | ally CV - enemy CV | |
| radar_diff | 预留 | 雷达船元数据不可得，留接口不实现 |

### c. 全新向量维度（追加到 feature_names 末尾，保持旧 20 维顺序不变）

向量新增 10 维：

    ally_class_wr, enemy_class_wr, class_wr_diff,
    ally_dd_wr, enemy_dd_wr, dd_wr_diff,
    ally_cv_wr, enemy_cv_wr, cv_wr_diff,
    ship_is_fresh_diff

ship_is_fresh_diff = ally 白板船人数 - enemy 白板船人数。单船的 ship_wr_shrunk
与 ship_battles 保持按玩家存储，聚合进 class 与 diff 特征，单船原始值
（ship_winrate 等）继续走 WeightedWinrate 兼容通道，不单独进向量，避免向量
膨胀。

dd_count_diff 与 cv_count_diff 依赖的 ship_type 在 battle_players 表已有，
直接从对局名单统计，与快照无关，存量 792 局立即可用。

## 未知口径

- 快照无 class_stats JSON 或 JSON 里没有本局舰种：class_wr_shrunk = UNKNOWN
  (-1)，class_battles = 0，聚合时跳过该玩家。
- has_stats 口径不变，分舰种缺失不影响账号级特征的可用性判定。

## 采集端口径

- WgPublicStatsProvider 新增 FetchAllShipStatsAsync：同 endpoint 去掉 ship_id
  过滤，一次请求返回玩家全部船只统计，按 ship_id 到舰种的映射聚合出每舰种
  battles/wins，并保留 battles 最大的单船数据。
- ship_id 到舰种的映射：WG ships/stats 响应不含舰种，需要额外数据源。方案为
  请求 `/wows/encyclopedia/ships/`（按 page 分页全量拉取一次，缓存到本地），
  或使用回放里自带的舰种。实现采用 encyclopedia 缓存方案，带 24 小时缓存与
  限流退避。
- player_snapshots 加一列 class_stats TEXT 存 JSON，形如
  {"BB": {"battles": 1000, "wins": 560}, ...}。权衡：十列方案查询直观但
  每加一类统计就要改表，JSON 列一次到位且旧行读取为 NULL 兼容。仓储层
  ALTER TABLE 迁移，旧行 JSON 为 NULL 时 class 特征为 UNKNOWN。
- 节流：全船只列表与现有单船查询同 endpoint，直接替换现有逐玩家单船查询的
  请求数（每玩家 1 次请求替代 1 次），不增加请求量。

## 分组折评估记录（2026-10-14）

库内 792 局 Random，快照全部为事后补采（19315 条，时间晚于全部对局），
严格防泄漏模式下 0 局可用，故评估用 allow_future 模式，结论偏乐观，
仅用于验证管线与特征方向性。

分组折口径：group_keys 按局参战者集合哈希，StratifiedGroupKFold 5 折，
656 组。逻辑回归 + 标准化。

| 特征集 | Brier | 对数损失 | 准确率 |
|---|---|---|---|
| 20 维基线 | 0.2118 | 0.6155 | 65.30% |
| 22 维（加 dd/cv_count_diff） | 0.2149 | 0.6230 | 64.84% |
| 32 维全量 | 0.2149 | 0.6230 | 64.84% |

- 收缩胜率类 8 维在存量数据上全为 0（快照无 class_stats），对指标无贡献，
  属预期。
- dd_count_diff 在存量数据上只有 4.3% 的局非零（分布 0:629, -1:15, 1:13），
  信号极稀疏，Brier 轻微变差在噪声范围内，方向性结论需等真实分舰种数据。
- 合成信号验证：向 class_wr 系列注入与标签相关的受控噪声后，32 维 Brier
  从 0.2118 降到 0.0040，证明新维度能被模型正常消费，管线无阻断。
- cv_count_diff 全为 0：当前对局库 CV 均衡分布，属数据现象，特征保留。

## 结论与后续

1. 特征管线两端契约已就位，等真实采集（快照带 class_stats）落地后，
   收缩胜率类特征自动生效，无需再改代码。
2. 建议采集端开 FetchClassStats 后重跑 snapshot 命令，积累赛前快照。
3. 重训练后需对比 20 维基线与 32 维的分组折指标，确认收缩特征有真实增益
   后再考虑让 ship_wr_shrunk 替代 WeightedWinrate 的 20% 混成通道。
