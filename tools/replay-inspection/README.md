# 回放解析验证工具

本目录存放独立于客户端的验证脚本，用于阶段一的回放解析验证。

## 背景

ApeRadar 只读取回放文件的第一层，也就是开头那段明文 JSON，拿到对局名单。真正的战斗数据在第二层与第三层：Blowfish 加密加 zlib 压缩的 BigWorld 网络包流，包含炮弹、伤害、击杀、占点、聊天。

要拿到每人的战后数据，必须解到这一层。方案是用 `landaire/wows-toolkit` 的 `replayshark`，它把回放转成结构化 JSON。

## 实测结论

已在 15.6.0.0 回放上验证通过。`replayshark -e <extracted> -c <constants> battle-results --format normalized` 能完整解出对局元数据、24 个玩家的战后统计，包括伤害、击杀、点亮、潜在伤害、存活时长、舰种等级。

详细输出结构见 `docs/replay-output-format.md`。

## 前置依赖

三项缺一不可。

### 1. replayshark 二进制

从 `landaire/wows-toolkit` 的 release 下载 `wows_toolkit_tools_<version>_win64.zip`，解压得到 `replayshark.exe`。工具包还含 `wowsunpack.exe`、`wows-data-mgr.exe`、`minimap_renderer.exe` 等。

不需要 Rust 工具链，用预编译二进制即可。

### 2. 游戏数据目录 extracted

`replayshark` 需要游戏的 `.def` 实体定义与 GameParams。游戏本体若已删除，从 `landaire/wows-replay-data` 拉取。

该仓库用内容寻址存储，整仓 8GB，但单版本解析只需少量对象。用 `fetch-minimal-gamedata.ps1` 精确下载：

```pwsh
./fetch-minimal-gamedata.ps1 -Version "15.6.0_12830008" -Scope scripts
```

`scripts` 范围只拉脚本定义与 GameParams，约 16MB。脚本读取 `metadata.toml` 的文件映射，按哈希逐个下载到 `common/`，并重建 `vfs/` 浏览树。

### 3. constants.json

从 `landaire/wows-replay-data` 的对应版本目录下载。缺失时 `replayshark` 会尝试联网拉取并失败。

## 用法

### 单份解析

```pwsh
./inspect-replay.ps1 -ReplayPath "<replay.wowsreplay>" `
  -ReplaysharkPath "<path to replayshark.exe>"
```

### 批量解析

```pwsh
./parse-replays.ps1 -ReplayDir "<replay dir>" `
  -ExtractedDir "<extracted dir>" `
  -ConstantsFile "<constants.json>" `
  -OutDir "<output dir>"
```

支持跳过已解析文件，便于中断续跑。

### 直接调用

```pwsh
replayshark.exe -e <extracted_dir> -c <constants.json> `
  battle-results --format normalized --out-dir <out_dir> <replay.wowsreplay>
```

注意 `-e` 与 `-c` 是全局参数，必须在子命令之前。

## 版本匹配

回放、extracted 数据、constants 三者版本必须一致。`replayshark` 会校验回放的 build 号与 extracted 目录的 `metadata.toml`，不匹配时直接报错，不会解析出错误结果。

本机回放为 build 12830008，对应版本目录 `15.6.0_12830008`。

## 已知边界

- 未完成或中断的对局，`played_duration` 为 0 且无 `battle_result`，需跳过。
- 单份解析约 3 到 5 秒，百份回放需要数分钟。
- `ship_name` 依赖翻译文件 `global.mo`，缺失时退化为船 ID 数字。
