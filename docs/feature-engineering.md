# 特征工程

这是项目的核心。模型的天花板由特征决定，而不是算法。本文档定义每个特征的含义、来源、计算方式与预期作用。

## 设计原则

**赛前可得。** 只使用开局前能拿到的信息。任何需要知道战斗过程才能算的量都不能进模型，否则训练时会数据泄漏，上线后拿不到。

**相对优于绝对。** 胜率、场次这类绝对值受服务器生态影响大。优先转成服务器内的相对位置。

**短期与长期分开。** 账号总胜率是长期水平，近期状态是短期波动，两者是独立信号，不要合并成一个数。

**可复现。** 每个特征的原始输入都要落盘快照，保证历史预测可回放重算。

## 特征分组

### 一、账号基础 AccountBaseline

来自统计源，长期水平的代理。

| 特征 | 说明 |
|---|---|
| account_battles | 账号总场次，衡量数据可靠度 |
| account_winrate | 账号总胜率 |
| account_winrate_solo | 单人胜率 |
| account_battles_solo | 单人场次 |
| account_winrate_div2 | 双排胜率 |
| account_battles_div2 | 双排场次 |
| account_winrate_div3 | 三排胜率 |
| account_battles_div3 | 三排场次 |
| weighted_winrate | 加权胜率，沿用 ApeRadar 口径 |
| pr | 个人评分，可空 |

加权胜率沿用 ApeRadar 的 `CalcWeightedWinrate`：单人场次权重 5、双排 2、三排 1，单船数据最多占两成权重。这个口径是兼容契约，不要改动，便于与现有工具对照。

场次同时作为可信度权重。一个 30 场 70% 胜率的账号，与一个 5000 场 55% 的账号，前者的胜率估计方差大得多。场次可以作为样本权重，或作为独立特征让模型自己学。

### 二、单船表现 ShipPerformance

| 特征 | 说明 |
|---|---|
| ship_battles | 该船场次 |
| ship_winrate | 该船胜率 |
| ship_avg_damage | 该船场均伤害 |
| ship_avg_exp | 该船场均经验 |
| ship_tier | 船等级 |
| ship_type | 舰种 |
| ship_is_premium | 是否金币船 |

单船数据很重要，因为玩家在不同船上的水平差异可以很大。一个总胜率 55% 的人，在他不熟悉的新船上可能打得很差。单船场次少时该特征可信度低，同样用场次加权。

### 三、近期状态 RecentForm

本项目的重点特征之一。账号总胜率是滞后指标，反映不出玩家这两周是状态火热还是连败上头。

| 特征 | 说明 |
|---|---|
| recent_20_winrate | 最近 20 场胜率 |
| recent_50_winrate | 最近 50 场胜率 |
| recent_20_avg_damage | 最近 20 场场均伤害 |
| recent_20_battles | 最近窗口实际场次，样本不足时降级 |
| winrate_delta_short_long | 近期胜率减长期胜率，衡量状态偏离 |
| form_trend | 近期状态的斜率，最近 10 场对最近 20 场 |

数据来源是 `player_snapshots` 与 `player_battle_records`。WG 公开 API 不提供逐场历史，只能靠本地采集与中央库积累。这是中央库存在的核心意义，它不只是缓存，它是近期状态特征的唯一来源。

样本不足时降级：近期场次少于阈值时，特征退化为长期胜率，并用 `recent_20_battles` 标记可信度。

### 四、会话状态 SessionState

比近期状态更细，捕捉单次游玩期间的状态漂移。

| 特征 | 说明 |
|---|---|
| session_battles | 本次会话已打场次 |
| session_winrate | 本次会话胜率 |
| consecutive_losses | 当前连续失败次数 |
| hours_since_session_start | 会话已持续小时数 |
| local_hour | 玩家当地时间的小时，捕捉时段效应 |

连续失败是上头信号。连败三场还继续排，往往伴随操作变形。会话边界由时间间隔界定，间隔超过阈值视为新会话。

本地小时需要时区信息，可从对局时间分布反推，或让用户设置。夜间时段的平均表现通常低于白天。

### 五、服务器归一化 ServerNormalization

本项目的重点特征之一，也最容易被忽略。

战舰世界没有公开 MMR，玩家水平分布在不同服务器差异明显。同样 52% 胜率，在不同服务器代表不同水平。绝对胜率不能跨服直接比较。

做法是把绝对胜率转成服务器内的相对位置：

```text
winrate_percentile = percentile_rank(account_winrate, server_baseline)
winrate_zscore = (account_winrate - server_mean) / server_std
```

`server_baselines` 表存各服务器的分布参数，定期从积累的数据重算。

| 特征 | 说明 |
|---|---|
| winrate_percentile | 账号胜率在本服的百分位 |
| winrate_zscore | 账号胜率在本服的 z 分数 |
| weighted_winrate_percentile | 加权胜率的百分位 |

模型应该用相对位置而不是绝对胜率，这样学到的才是"这个人在本服排前 30%"这种可比信息。

### 六、组队结构 DivisionStructure

本项目的重点特征之一。ApeRadar 的加权胜率已经把组队因素折进去了，但那是一个综合数字。模型应该看到各组队形式各自的原始表现。

| 特征 | 说明 |
|---|---|
| current_division_size | 本局该玩家的组队规模 |
| division_solo_winrate | 历史单人胜率 |
| division_div2_winrate | 历史双排胜率 |
| division_div3_winrate | 历史三排胜率 |
| division_mate_winrate | 同队队友的历史胜率，可空 |
| team_division_count_2 | 本方双排组数 |
| team_division_count_3 | 本方三排组数 |

三排的配合默契是账号胜率体现不出来的额外信息。一队里有两组三排，与全是散人，纸面胜率可能一样，实际协同能力差别很大。

### 七、队伍聚合 TeamAggregation

把单个玩家的特征聚合成队伍层面的特征。

| 特征 | 说明 |
|---|---|
| team_avg_winrate | 队伍平均胜率 |
| team_avg_weighted_winrate | 队伍平均加权胜率 |
| team_avg_recent_winrate | 队伍平均近期胜率 |
| team_avg_battles | 队伍平均场次 |
| team_top_winrate | 队伍最高胜率，衡量是否有大腿 |
| team_bottom_winrate | 队伍最低胜率 |
| team_unicum_count | 队伍中高胜率玩家数，海王数 |
| team_ape_count | 队伍中低胜率玩家数，猴子数 |
| team_hidden_count | 队伍中隐藏战绩人数 |
| team_avg_tier | 队伍平均船等级 |

阈值沿用 ApeRadar 默认口径：低于 47% 为猴，高于 58% 且场次超 500 为海王。这些阈值可配置，但作为默认口径保持稳定便于对照。

### 八、差分特征 Difference

预测是二分类问题，真正有判别力的是双方差距，不是各自的绝对值。

| 特征 | 说明 |
|---|---|
| winrate_diff | 双方平均胜率差 |
| weighted_winrate_diff | 双方平均加权胜率差 |
| recent_winrate_diff | 双方平均近期胜率差 |
| battles_diff | 双方平均场次差 |
| unicum_count_diff | 双方海王数差 |
| ape_count_diff | 双方猴子数差 |
| tier_avg_diff | 双方平均船等级差 |

差分特征通常比绝对值更稳定，是逻辑回归基线的主力。

### 九、对局上下文 Context

| 特征 | 说明 |
|---|---|
| battle_type | 对局类型，Random/Ranked/Coop |
| map_id | 地图，某些地图有阵营优势 |
| team_size | 队伍人数 |
| server | 服务器 |

## 缺失值与降级

隐藏战绩、新账号、无本地历史，都会导致特征缺失。处理策略：

- 隐藏战绩：标记 `is_hidden`，胜率特征置缺失，用该服务器的平均胜率填充，并让模型通过缺失标记学习。
- 新账号：场次少，胜率方差大，用场次作为可信度权重。
- 无本地历史：近期状态与会话状态缺失，退化为长期特征，用 `features_available` 标记完整度。

任何降级都要显式记录，不能悄悄用默认值掩盖。

## 特征快照

每次预测都要把完整特征向量存进 `predictions.feature_json`。理由有三：

1. 预测可事后完全复现，校准才可信。
2. 模型迭代后能用历史特征重放评估，不必重跑采集链路。
3. 特征工程的 bug 可以在历史数据上回查。

## 待验证的假设

以下假设需要在数据积累后实证检验，不能想当然：

- 近期 20 场胜率相对账号总胜率，是否真的提升预测准确率。
- 服务器归一化是否显著改善跨服数据的可比性。
- 会话状态特征是否有超出近期状态的边际贡献。
- 组队结构的原始特征是否优于加权胜率这一综合数字。

这些验证要在校准框架就位后进行，用数据说话。
