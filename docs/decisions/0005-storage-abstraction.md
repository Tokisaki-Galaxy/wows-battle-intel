# ADR 0005：持久层藏在仓储接口后

- 状态：已采纳
- 日期：2026-10-14

## 背景

后端起步用 Cloudflare D1，但 D1 有单库十 GB 上限，且逐场对局记录只增不减。数据规模必然触顶，届时需要迁移到 Neon Postgres 或其他持久层。

如果业务代码直接调 D1 API，迁移意味着大规模重写。

## 决策

所有数据访问藏在 `IBattleRepository` 接口后。业务代码只依赖接口，不依赖具体存储。

起步实现 `D1BattleRepository`，迁移目标 `NeonBattleRepository`。两者实现同一接口，业务代码零改动。

本地客户端同理，`SqliteLocalStore` 实现自己的仓储接口。

## 理由

**迁移成本可控。** D1 触顶时，只需新增一个实现并切换绑定，不动业务逻辑。

**测试友好。** 可以用内存实现或替身做单元测试，不依赖真实数据库。

**关注点分离。** 业务逻辑不应关心数据存在哪、怎么查。

**租约风险对冲。** 云服务商的配额与定价会变。抽象层让技术选型可以随时调整。

## 接口设计

```csharp
public interface IBattleRepository
{
    Task SaveBattleAsync(BattleRecord battle, CancellationToken ct = default);
    Task SavePredictionAsync(PredictionResult prediction, CancellationToken ct = default);
    Task<BattleRecord?> GetBattleAsync(string battleId, CancellationToken ct = default);
    Task BackfillOutcomeAsync(string battleId, BattleOutcome outcome, CancellationToken ct = default);
    Task<IReadOnlyList<PredictionResult>> GetUncalibratedPredictionsAsync(int limit, CancellationToken ct = default);
}
```

接口保持窄，只暴露业务需要的方法。查询能力通过专门的方法暴露，不做通用查询接口，避免泄漏具体存储的语义。

## 代价

**多一层抽象。** 简单的查询也要绕一层。可接受，本项目的查询复杂度有限。

**接口设计要前瞻。** 接口一旦定下，改起来涉及所有实现。需要预留合理的查询能力。

**可能过度设计。** 如果永远不迁移，这层抽象是浪费。但考虑到 D1 的硬上限是已知的，迁移是可预见的，抽象是必要的。

## 实现要点

- 接口方法尽量用领域模型，不暴露存储特有的类型。
- 幂等性由实现保证，接口层面约定。
- 本地与云端接口可以分离，因为查询需求不同。

## 参考

ApeRadar 的 `IPlayerStatsAdapter` 是同类思路的现成范例：把数据源抽象成接口，Vortex、WG Public、Yuyuko 各自实现。本项目的统计层直接沿用这个模式。
