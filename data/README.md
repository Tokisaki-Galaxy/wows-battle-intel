# 本地数据目录

本目录存放体积大、可从上游重建的数据，**不入库**。除本说明文件外，`data/` 下所有内容都被 `.gitignore` 排除。

放在这里而不是临时目录的原因：临时目录会被系统清理，而这些数据重建需要重新下载数百 MB。

## 目录结构

```text
data/
├── gamedata/        游戏数据，.def 实体定义与 GameParams，按版本分子目录
├── constants/       各版本的 constants.json，replayshark 需要
├── tools/           replayshark 等命令行工具二进制
├── parsed/          replayshark 解析产物，每个回放一个 JSON
├── battles.db       本地 SQLite 库，已导入的对局与玩家快照
└── README.md        本文件
```

## 各项来源与重建

### gamedata/

来源 `landaire/wows-replay-data`。该仓库用内容寻址存储，整仓 8GB，但单版本解析只需几十个脚本定义与 GameParams 对象。

用 `tools/replay-inspection/fetch-minimal-gamedata.ps1` 精确下载：

```pwsh
./tools/replay-inspection/fetch-minimal-gamedata.ps1 -Version "15.6.0_12830008" -Scope scripts `
  -DestDir "<仓库>\data\gamedata"
```

每个版本约 16MB。已准备的版本：15.6.0、15.7.0、15.8.0。

### constants/

来源同上仓库对应版本目录下的 `constants.json`。缺失时 replayshark 会尝试联网拉取并失败，因此必须本地提供。

### tools/

来源 `landaire/wows-toolkit` 的 release，下载 `wows_toolkit_tools_<版本>_win64.zip` 解压。含 `replayshark.exe`、`wowsunpack.exe`、`wows-data-mgr.exe`、`minimap_renderer.exe` 等。

不需要 Rust 工具链。

### parsed/

`replayshark battle-results --format normalized` 的输出，文件名格式为 `<版本>_<回放名>.json`，带版本前缀避免跨版本重名。

用 `tools/replay-inspection/parse-all-replays.ps1` 批量生成。

### battles.db

本地 SQLite 库。用 CLI 的 `import` 子命令从 `parsed/` 导入，用 `snapshot` 子命令采集玩家统计。

## 回放文件本身

回放不在本目录，仍在游戏安装目录下：

```text
D:\SteamLibrary\steamapps\common\World of Warships\replays\<版本>\
```

解析脚本直接读取那里，产物写入 `data/parsed/`。

## 版本匹配

回放、gamedata、constants 三者版本必须一致。replayshark 会校验回放的 build 号与 gamedata 的 `metadata.toml`，不匹配时直接报错，不会产生错误结果。

已准备的三套数据对应关系：

| 游戏版本 | gamedata 目录 | 回放数量 |
|---|---|---|
| 15.6.0 | 15.6.0_12830008 | 120 |
| 15.7.0 | 15.7.0_13015811 | 191 |
| 15.8.0 | 15.8.0_13187581 | 183 |
