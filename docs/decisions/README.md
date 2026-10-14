# 架构决策记录

本目录存放重要的技术决策，每份记录一个问题、一个结论、以及理由与代价。格式参考轻量版 ADR。

## 索引

| 编号 | 标题 | 状态 |
|---|---|---|
| [0001](0001-desktop-client-csharp-dotnet10.md) | 桌面客户端用 C# 与 .NET 10 | 已采纳 |
| [0002](0002-cloudflare-workers-backend.md) | 后端采用 Cloudflare 全家桶 | 已采纳 |
| [0003](0003-local-inference.md) | 预测在客户端本地推理 | 已采纳 |
| [0004](0004-replay-parse-via-replayshark.md) | 回放解析通过 replayshark 外部进程 | 已采纳 |
| [0005](0005-storage-abstraction.md) | 持久层藏在仓储接口后 | 已采纳 |
| [0006](0006-central-package-management.md) | 集中式包版本管理 | 已采纳 |

## 格式

每份记录包含：

- **状态**：提议中、已采纳、已废弃、被取代。
- **背景**：面临什么问题，有哪些约束。
- **决策**：选择了什么。
- **理由**：为什么这样选。
- **代价**：这样选的代价与限制。
