# ADR 0010：回放解析直接读游戏安装目录，不再随包分发游戏数据

- 状态：已采纳
- 日期：2026-10-17

## 背景

回放解析依赖 replayshark，而 replayshark 需要游戏的操作类型定义与
GameParams。早期做法是用 wowsunpack 把游戏数据预先提取到 `data/gamedata`
与 `data/constants`，再以 `-e` 与 `-c` 交给它。发布脚本于是把这两个目录
整个塞进分发包。

这套做法有两个问题：

第一，包体持续膨胀。提取目录按游戏版本各存一份，`data/gamedata` 累积到
163 MB，其中 `common` 下 80 MB 是 DDS 贴图（渲染用），`15.9.0` 里
`GameParams.data` 还存了两份完全相同的副本。绿色 zip 达到 150 MB，
Velopack 打包时要把整份内容复制到系统临时目录，C 盘空间一紧就打包失败。

第二，数据陈旧就会解析失败。游戏更新后必须重新提取，否则新版本回放
找不到对应目录。实测还发现 `-e` 模式下 `ship_name` 返回的是数字 id，
`map` 返回 `spaces/44_Path_warrior` 这种内部路径，界面上直接露出 id。

## 决策

改用 replayshark 的 `-g <游戏目录>` 参数，让它从游戏自己的
`res_packages\*.pkg` 里直接读数据。

- `ReplaysharkOptions` 以 `GamePath` 取代 `ExtractedDataPath`，
  `ConstantsPath` 保留为可选覆盖。
- `ReplayService` 只校验游戏目录是否就绪，删掉按回放版本挑目录的逻辑。
- `AppConfig` 删除 `ExtractedDataPath` 与 `ConstantsRootPath` 两个字段。
- 发布脚本不再打包 `gamedata` 与 `constants`，只带 `replayshark.exe`。

## 理由

**数据永远与游戏同步。** 游戏更新后 `bin\<build>` 与 `res_packages` 一起
更新，解析器无需任何额外步骤就能读新版本，不存在"忘了提取"的状态。

**输出更可用。** `-g` 模式下地图返回 `Warrior's Path`、舰名返回 `Napoli`，
直接可展示，不必再维护一套 id 到名称的映射表。

**包体从 150 MB 降到 7 MB。** Velopack 包 9.1 MB，普通 zip 压缩后 7 MB。
系统临时目录的磁盘压力随之消失。

## 代价

**解析依赖本机装了游戏本体。** 卸载游戏后历史回放无法解析。这个前提对
本工具天然成立：它本来就要读游戏目录下的 `replays\tempArenaInfo.json`
做实时观测，设置页已经在探测游戏目录。回放解析与实时观测共用同一个前提，
没有新增约束。

**`common` 目录下的 `.def` 定义在 `-g` 模式下不复用。** 之前提取的数据里
包含了跨版本共享的定义，现在完全由 replayshark 从游戏包内取。若将来需要
离线解析，再补一条下载链路即可，届时可参照本 ADR 恢复 `-e` 路径。
