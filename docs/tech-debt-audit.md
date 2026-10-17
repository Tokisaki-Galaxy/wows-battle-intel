# 技术债务审计记录

审计分支：`chore/tech-debt-audit`，基线 `master@04a8b6a`。

本轮关注三类问题：同一逻辑在多个位置各写一份、本该走已有抽象却另有实现、
以及沉淀下来的一次性写法。每条改动都保持行为等价，除非下面明确记为修正。

## 已收口的重复实现

| 问题 | 原先分散在 | 收口位置 |
| --- | --- | --- |
| 分舰种存储键与展示代号 | `SnapshotFeatureBuilder`、`Cli`、`PlayerRowViewModel`、`WgPublicStatsProvider` | `ShipClassExtensions.ToStorageKey` / `ToDisplayCode` |
| 服务器与对局类型映射 | `ArenaInfoParser` 与 `ReplaysharkMapper` 各一份，靠注释互相提醒 | `GameServerExtensions.ParseRealm` / `BattleTypeExtensions.FromGameType` |
| 快照合并的仓库查找 lambda | `MainViewModel` 四处逐字相同 | `MainViewModel.EnrichWithSnapshots` |
| 战绩完备判据与候选过滤 | `MainViewModel` 与 `CachingPlayerStatsProvider` 各一份 | `PlayerRecord.IsFullyCovered` / `IsStatsCandidate` |
| 429 退避重试与默认 HttpClient | `WgPublicStatsProvider`、`ClanCatalogService`、`UpdateService` 内两份 | `WgHttp`、`UpdateServiceHttp.Client` |
| 应用数据目录拼接 | 配置路径、数据库、日志、更新时间戳四处 | `AppPaths` |
| 玩家档位阈值 | `PlayerRowViewModel` 硬编码 0.58/0.47/500 | `TeamAggregator.ClassifyPlayer` 与 `PlayerTier` |
| 边缘响应构造与错误信封 | 入口文件定义 `json`，handler 反向 import 入口 | `http.ts`，handler 不再依赖入口 |
| 表名、路径前缀、结果字符串、条数上限 | 各 handler 内散落 | `constants.ts` |
| 特征名与向量键两张平行表 | `features.py` 里 `to_vector` 与 `feature_names` 各写一份 | `FEATURE_SPECS` 单一来源 |

## 顺带修正的正确性问题

**列表与详情显示两个不同的胜率。** 列表用 `P0` 直接格式化原始概率，详情区走
`ClampForDisplay`，模型外推到 0.997 时同一局会显示 100% 与 95.0%。列表改为同口径夹取。

**边缘上报接口不校验请求体。** 原实现把 `request.json()` 的结果直接断言成
`BatchUploadRequest` 入库，任意客户端可写任意 `result` 与 `accountId`；JSON 解析失败还会
被兜成 500。现在逐字段校验，并限制单批对局数、单场参战者数、`startedAt` 必须为正。

**近期胜率的聚合口径失真。** `player_stats.recent_battles` 是无上限累加，等同全生涯计数，
`stats.ts` 却把它当近 20 场胜率返回。改为读取端按逐场记录现算时间窗，聚合表不再维护
`recent_*`；同一账号多服时取最近更新的一行。配套迁移 `0002` 补索引。

**`ToClassKey` 的输入面。** 委托 `ShipClassExtensions.Parse` 后一度放行 `DD`/`CA`/`CL` 缩写，
`CL` 会被并入 `CA` 键导致分舰种聚合多算一类。已收窄为只认 encyclopedia 返回的完整类型名。

## 明确不改的部分

- `RelayCommand` 的 `async void` 是 `ICommand` 契约要求，不是债务。
- `SnapshotFeatureBuilder.Merge` 这类逐字段列出的写法能防止新增字段被静默丢弃，改成反射反而危险。
- `Theme.xaml` 的兼容键加 DataTrigger 分层是设计，不是魔法字符串。
- CLI 里的 `GetAwaiter().GetResult()` 属于同步入口的正常用法。
- `WgApiParser.Describe` 是公开的调试格式化方法，保留。

## 仍待处理

- `SnapshotFeatureBuilder.Enrich` 是同步签名，客户端 `EnrichWithSnapshots` 因此在界面线程上
  同步等待 2×N 次 SQLite 往返。改成异步需要连带调整 Core 的测试，建议单独排期。
- C# 与 Python 两侧共享的常量（ApeRadar 阈值、近期窗口、逻辑斜率）目前靠注释对齐，尚无
  由构建生成的单一真源。
- `src/ml` 的 ONNX 导出测试依赖 `skl2onnx`，本机环境未安装时 4 条用例失败，属环境问题，
  CI 会装完整依赖。
