# 回放解析验证工具

阶段一的目标是证明能把 `.wowsreplay` 解成结构化数据。本目录存放独立于客户端的验证脚本。

## 背景

ApeRadar 只读取回放文件的第一层，也就是开头那段明文 JSON，拿到对局名单。真正的战斗数据在第二层与第三层：Blowfish 加密加 zlib 压缩的 BigWorld 网络包流，包含炮弹、伤害、击杀、占点、聊天。

要拿到每人的战后数据，必须解到这一层。方案是用 `landaire/wows-toolkit` 的 `replayshark`，它把回放转成 JSON lines。

## 安装 replayshark

`replayshark` 是 Rust 工具，从 wows-toolkit 发布页下载现成二进制最省事。若需自行编译，需要 Rust 工具链。

本机未配置 Rust。优先寻找现成的 Windows 二进制。

## 用法

```pwsh
# 把一份回放转成 JSON lines
./replayshark dump path/to/replay.wowsreplay > output.jl

# 输出的第一行是元数据，后续每行是一个网络包
```

JSON lines 的字段：

- 第一行：文件头的元数据，含玩家列表、地图、对局类型。
- 后续行：`clock` 时间戳加 `payload` 包体。

## 脚本

| 脚本 | 用途 |
|---|---|
| `inspect-replay.ps1` | 调用 replayshark，输出结构与统计摘要 |
| `sample-replays.md` | 本机可用的测试回放清单 |

## 版本匹配

回放文件与解析器必须版本对应。游戏大版本更新会改变 `.def` 定义，旧解析器解不了新回放，反之亦然。

本机回放主要是 15.6.0.0。需确认 `replayshark` 的 gamedata 覆盖该版本。

## 已知问题

待验证：
- `replayshark` 是否原生支持 Windows 无 Rust 环境运行。
- 15.6.0.0 回放能否被当前版本解析。
- JSON lines 里战后结果（胜负、每人伤害）的字段结构。
