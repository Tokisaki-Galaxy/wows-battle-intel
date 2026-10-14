# ADR 0001：桌面客户端用 C# 与 .NET 10

- 状态：已采纳
- 日期：2026-10-14

## 背景

需要一个 Windows 桌面客户端，负责观测回放目录、解析回放、拉取玩家统计、本地推理、展示对照。

现有资源是 ApeRadar，一个开源的《战舰世界》水表工具，C# WPF 编写，MIT 协议。它已经实现了统计源适配、关注名单、战场计算、输出文本生成，扩展点清晰，有 `BattleLoadCoordinator`、`IPlayerStatsAdapter`、`Battlefield` 等可复用的结构。

候选语言有 C#、Rust、Python。Rust 有 wows-toolkit 的现成生态，但本机未配置工具链。Python 有成熟的机器学习生态，但不适合做 Windows 桌面 GUI。

## 决策

桌面客户端采用 C# 14 与 .NET 10，WPF。

## 理由

**复用现有代码。** ApeRadar 的架构与本项目需求高度重合，直接扩展省去从零搭客户端的成本。语言与框架一致，扩展点直接可用。

**语言与版本匹配。** ApeRadar 源码的目标框架是 `net10.0-windows`，对应 C# 14。本机已安装 .NET 10 SDK。同一套工具链同时覆盖客户端与测试。

**.NET 10 是当前 LTS。** 支持到 2028 年 11 月。相比之下 .NET 8 于 2026 年 11 月到期，新项目不应起步于即将过期的版本。

**工具链完备。** 异步、LINQ、HttpClient、SQLite、ONNX Runtime 绑定、WPF 数据绑定，全部原生或有一流支持。

## 代价

- 客户端绑定 Windows，无法跨平台。对本项目不是问题，游戏本身只在 Windows 运行。
- WPF 相对现代 Web UI 框架，界面开发效率较低。可接受，本项目界面复杂度有限。
- 需要用户安装 .NET 运行时，或打包为自包含发布。

## 备选方案

**Rust 加 Tauri。** 可以复用 wows-toolkit 的 crate，界面用 Web 技术，跨平台。代价是客户端要从零重写，需要配置 Rust 工具链，ApeRadar 的现成结构全部作废。收益不足以抵消成本。

**Python 加 GUI。** 机器学习生态最好，但桌面 GUI 体验差，且需要把 ApeRadar 的逻辑全部移植。不划算。

## 后续

客户端保持 Windows 专属。回放解析暂不链入 Rust crate，改用外部进程，见 ADR 0004。
