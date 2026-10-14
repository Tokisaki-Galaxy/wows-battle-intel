# ADR 0004：回放解析通过 replayshark 外部进程

- 状态：已采纳
- 日期：2026-10-14

## 背景

`.wowsreplay` 的完整解析涉及 BigWorld 网络协议、Blowfish 解密、zlib 解压、`.def` 实体定义匹配。这是领域里最脏的活，且每次游戏大版本更新都要跟着更新定义文件。

现有方案：

- `landaire/wows-toolkit` 的 `replayshark`，Rust 编写，命令行工具，`dump` 输出 JSON lines。
- `Nodsoft/WoWS-ReplaysUnpack`，纯 C# 的 .NET 库，可进程内引用，但只支持 0.10.10 以后的回放。
- `Monstrofil/replays_unpack`，Python，生态源头。

## 决策

近期用外部进程方式：调 `replayshark dump`，读 JSON lines。解析器抽象为 `IReplayParser` 接口，外部进程实现为 `ReplaysharkReplayParser`。

长期若需要进程内解析，加一个引用 `Nodsoft.WoWS-ReplaysUnpack` 的 `NodsoftReplayParser` 实现，接口不变。

## 理由

**零移植成本。** 不用重写 BigWorld 协议解析，不用维护 `.def` 定义匹配，这些脏活由 wows-toolkit 承担。

**版本兼容有人维护。** wows-toolkit 团队持续跟进游戏更新。自己维护这套数据会非常痛苦。

**今天就能跑通。** 拿现成的二进制，对着本机回放跑一次即可验证。不阻塞后续开发。

**接口隔离。** 外部进程的调用细节被 `IReplayParser` 封装。将来换进程内实现，上层零改动。

**语言无关。** `replayshark` 输出 JSON，C# 直接读，不需要绑定 Rust。

## 代价

**外部依赖。** 需要随客户端分发 `replayshark` 二进制。

**进程启动开销。** 每次解析要起一个进程。可接受，解析是低频操作，每局一次。

**错误处理复杂。** 要处理进程崩溃、超时、输出格式变化。

**版本匹配。** 回放版本与解析器版本必须对上，否则解析失败。本机回放是 15.6.0.0，需确认 `replayshark` 与 gamedata 支持。

## 实现要点

- `CanParseAsync` 先探测回放版本是否受支持，不支持时提前跳过而非解析到一半失败。
- 进程调用设超时，避免挂死。
- 解析失败抛 `ReplayParseException`，编排层决定是否重试。
- 原始 JSON 存进 `battle_players.raw_json`，便于回溯与格式变化应对。

## 风险

本机回放是 15.6.0.0。若 `replayshark` 的 gamedata 尚未覆盖该版本，解析会失败。阶段一的首要任务就是验证这一点。若版本不匹配，需等待 gamedata 更新，或降级到 `replays_unpack` 的 Python 实现。

## 备选方案

**直接用 Rust crate。** 把 `wows-replays` 链进程序。需要 Rust 工具链与 FFI 或重写客户端，成本过高。

**用 Nodsoft 的 C# 库。** 进程内解析，无外部依赖，是长期更优解。但只支持 0.10.10 以后，且功能不如 `replayshark` 全。作为备选而非起步方案。
