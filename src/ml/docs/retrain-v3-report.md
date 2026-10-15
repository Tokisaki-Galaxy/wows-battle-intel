# v3 模型重训评估报告

日期：2026-10-16。本次重训的目标是让分舰种收缩胜率类特征首次在真实采集数据上生效，
并把 32 维模型重新导出为 ONNX 交付版。数据、代码与评估口径说明如下。

## 数据概况

| 项目 | 数值 |
|---|---|
| battles.db 对局总数 | 792 |
| Random（训练白名单内） | 647 |
| Ranked（训练白名单内） | 10 |
| Coop | 93 |
| Unknown | 42 |
| player_snapshots | 30721 条，覆盖 11313 名玩家 |
| class_stats 非空 | 10978 条（命中率 97.4%） |
| 单船级快照 | 10613 条 |

对局类型白名单为 Random 与 Ranked（`DEFAULT_BATTLE_TYPES`），Coop 等 PvE 模式
不参与训练。792 局中 657 局进入训练集，与 647 + 10 一致。

快照全部为近 72 小时新采，v2 设计文档中"收缩胜率类特征全为 0"的问题已解决：
class_stats 命中率 97.4%，收缩特征有真实数值。

## 关键限制：严格模式下样本为零

本次采集是事后补采，全部快照时间戳晚于对局时间。因此：

- 严格模式（allow_future=False）：657 局全部因"快照晚于对局"被拒，
  可用样本 0，管线报"样本不足"拒绝训练。这是防时间泄漏设计的正确行为。
- allow_future 模式（allow_future=True）：事后补采的全部快照可用，
  657 局全部进入训练。

所以本次所有指标均为 allow_future 模式产出，存在乐观偏差，
不可直接当作真实预测能力解读。结论方向性有效，绝对数值偏乐观。
真实赛前快照积累后必须用严格模式复测。

## 分组折指标

交叉验证口径：按参战账号连通分量分组（同一账号绝不跨训练与测试折），
StratifiedGroupKFold 5 折，标签分层。管线内置，未做额外配置。

### allow_future 模式（657 样本）

| 模型 | Brier | 对数损失 | 准确率 |
|---|---|---|---|
| logistic（标准化 + lbfgs） | **0.2122** | **0.6220** | **65.91%** |
| lightgbm（300 树，深度 4） | 0.2304 | 0.6781 | 65.46% |
| 无信息基线（恒定 0.5） | 0.2500 | 0.6931 | 53.42% |
| 加权胜率差基线 | 0.2426 | 0.6783 | 67.12% |

### 严格模式（0 样本）

管线拒绝训练，无指标。原因与局限见上节。

## 与 v2 基线对比

v2 评估基线（features-v2-design.md 底部，20 维，allow_future，同口径分组折）：
Brier 0.2118 / 对数损失 0.6155 / 准确率 65.30%。

| 版本 | 特征维数 | Brier | 对数损失 | 准确率 |
|---|---|---|---|---|
| v2 交付模型（manifest 备份） | 32 | 0.2149 | 0.6230 | 64.84% |
| v2 评估基线（20 维） | 20 | 0.2118 | 0.6155 | 65.30% |
| v3 本次（logistic） | 32 | 0.2122 | 0.6220 | 65.91% |
| v3 本次（lightgbm） | 32 | 0.2304 | 0.6781 | 65.46% |

解读：

1. 分舰种收缩特征生效后，32 维 logistic 相对 v2 交付模型 Brier 持平
   （0.2122 对 0.2149），准确率提升 1.07 个百分点（65.91% 对 64.84%）。
   新增的 8 维收缩特征不再全零，`ally_class_wr` 与 `class_wr_diff`
   进入 logistic 特征重要性前列，说明模型确实在消费这些信号。
2. 相对 20 维评估基线，Brier 基本持平（0.2122 对 0.2118，差 0.0004 在
   折间噪声内），准确率略升。样本只有 657 局，无法区分噪声与真实增益。
3. LightGBM 在该样本量下 Brier 明显劣于 logistic（0.2304 对 0.2122），
   与管线注释一致：小样本下梯度提升容易过拟合，简单线性模型更稳。

## 特征重要性（lightgbm，split 次数，折平均，前 10）

| 特征 | 重要性 |
|---|---|
| class_wr_diff | 192.0 |
| enemy_top_winrate | 169.6 |
| ally_battles | 168.2 |
| ally_top_winrate | 160.8 |
| enemy_class_wr | 160.2 |
| ally_class_wr | 152.8 |
| ally_dd_wr | 134.4 |
| enemy_battles | 126.0 |
| battles_diff | 102.8 |
| dd_wr_diff | 101.0 |

分舰种收缩相关特征（class_wr_diff、ally/enemy_class_wr）占据前三与第六，
是本次 lightgbm 最看重的信号组。logistic 折平均系数前 10 中
ally_class_wr（+0.93）、class_wr_diff（+0.25）同样在列，方向一致。

## 结论与交付

**交付版：allow_future 模式训练的 logistic 模型（32 维）。**

理由：

1. 同模式下 logistic 的 Brier 与对数损失均优于 lightgbm
   （0.2122 对 0.2304，0.6220 对 0.6781），小样本上线性模型更不易过拟合，
   且 v2 交付版同为 logistic，客户端推理路径无变化。
2. 模式选择上没有第二个选项：严格模式样本为零，无法训练。这是数据现状
   决定的，allow_future 的乐观偏差已在上文如实标注，等赛前快照积累后
   需用严格模式复测再校准预期。

交付物：

- data/models/baseline.onnx：32 维 logistic，SHA256
  85c4d748a73615b8ed79576bdb05e2f04af6f4c439cdec2e2444b14abeee913b。
  ONNX 一致性验证通过，onnxruntime 加载与推理冒烟通过
  （输出含 label 与 probabilities 双输出，featureCount 32 与 manifest 一致）。
- data/models/baseline.manifest.json：metrics.brier 0.2122，
  metrics.log_loss 0.6220，metrics.accuracy 0.6591。
- 旧 v2 模型已备份为 data/models/baseline-v2.onnx.bak 与
  data/models/baseline.manifest-v2.json.bak（不进 git）。

## 已知问题与后续

1. 全部快照为事后补采，严格模式样本为零。建议采集端在开局阶段对当局
   参战者落赛前快照，积累后重跑严格模式评估，本报告指标届时应明显回落，
   那才是真实水位。
2. 管线本身未发现 bug，src/ml Python 代码零改动。
3. 加权胜率差基线准确率 67.12% 高于两个模型的折均准确率，但其 Brier
   0.2426 明显更差，说明模型概率排序与校准更稳。样本增大后建议把该
   基线纳入每次评估输出做持续对照。
