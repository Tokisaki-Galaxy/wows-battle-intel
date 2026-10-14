# 回放解析输出结构

本文档记录 `replayshark battle-results --format normalized` 的真实输出结构，基于本机 15.6.0.0 回放实测。这是 C# 解析适配层的字段映射依据。

## 调用方式

```pwsh
replayshark.exe -e <extracted_dir> -c <constants.json> `
  battle-results --format normalized --out-dir <output_dir> <replay.wowsreplay>
```

三个参数缺一不可：

| 参数 | 作用 | 缺失后果 |
|---|---|---|
| `-e/--extracted` | 游戏数据目录，含 `.def` 定义与 GameParams | 报错 `entity specs` 无法加载 |
| `-c/--constants` | constants.json，覆盖消耗品 ID 等常量 | 报错 `failed to initialize constants resolver`，它会尝试联网拉取 |
| `--out-dir` 或 `--out-file` | 输出目标 | 报错 `one of --out-dir or --out-file is required` |

注意 `-e` 与 `-c` 是全局参数，必须放在子命令 `battle-results` 之前。

## 顶层结构

输出是一个 JSON 对象，两个字段。

```json
{
  "metadata": { ... },
  "players": [ ... ]
}
```

### metadata

战斗元数据。

| 字段 | 类型 | 说明 |
|---|---|---|
| map | string | 地图路径，如 `spaces/50_Gold_harbor` |
| game_mode | string | 模式，如 `domination_4point` |
| game_type | string | 对局类型，如 `RandomBattle` |
| match_group | string | 分组，如 `pvp` |
| version | object | 游戏版本，含 major/minor/patch/build |
| max_duration | number | 最大时长秒 |
| played_duration | number | 实际时长秒 |
| extra_duration | number | 加时秒 |
| timestamp | string | 开始时间，ISO 8601 UTC |
| battle_result | object | 战斗结果，含 type 与 team_id |
| results_battle_result | object | 结果来源，含 type 与 team_id |

`battle_result.type` 取值 `Win` 或 `Loss`，是录制者视角的胜负。`team_id` 是录制者所在队伍。

注意 `played_duration` 为 0 且 `battle_result` 缺失的情况，代表对局中断或未完成，应跳过。

### players 数组

24 个参战者，每项是一个玩家记录。字段极丰富，下面按用途分组。

## 玩家字段

### 身份与队伍

| 字段 | 类型 | 说明 |
|---|---|---|
| db_id | number | 账号 ID，对应 WG API 的 account_id |
| name | string | 昵称 |
| display_name | string | 显示昵称 |
| clan | string | 公会标签 |
| clan_color_rgb | number | 公会颜色 |
| realm | string | 服务器，如 ASIA |
| team_id | number | 队伍编号，与 metadata.battle_result.team_id 比较可判断友敌 |
| relation | number | 关系，0/1 为友，2 为敌 |
| division_id | number/null | 组队 ID，null 表示单排 |
| division_label | string/null | 组队标签 |
| is_self | bool | 是否为录制者 |
| is_bot | bool | 是否机器人 |
| is_abuser | bool | 是否被标记滥用 |
| is_hidden_profile | bool | 是否隐藏战绩 |

组队规模可由相同 `division_id` 的玩家数推算，这是组队结构特征的来源。

### 船只信息

| 字段 | 类型 | 说明 |
|---|---|---|
| ship_index | string | 船的内部索引，如 `PASS710` |
| ship_id | number | 船 ID 哈希值 |
| ship_name | string | 船名，如 `Archerfish`，来自翻译文件 |
| ship_nation | string | 国籍，如 `Russia` |
| ship_class | string | 舰种，如 `Submarine`、`Battleship`、`Cruiser`、`AirCarrier` |
| ship_tier | number | 等级 |
| is_test_ship | bool | 是否测试船 |

`ship_class` 的取值与项目 `ShipClass` 枚举的映射：

| 游戏值 | 枚举 |
|---|---|
| Destroyer | Destroyer |
| Cruiser | Cruiser |
| Battleship | Battleship |
| AirCarrier | Carrier |
| Submarine | Submarine |

### 战后统计 server_results

这是核心数据，来自服务器权威结果。

| 字段 | 类型 | 说明 |
|---|---|---|
| xp | number | 获得经验 |
| raw_xp | number | 原始经验 |
| damage | number | 造成伤害 |
| hits | number | 命中数 |
| spotting_damage | number | 点亮伤害 |
| potential_damage | number | 潜在伤害 |
| received_damage | number | 承受伤害 |
| kills | number | 击杀数 |
| fires_dealt | number | 造成起火数 |
| floods_dealt | number | 造成进水数 |
| citadels_dealt | number | 核心区命中数 |
| crits_dealt | number | 击穿数 |
| distance_traveled | number | 航行距离 |

细分字段：

- `damage_details`：按弹种拆分的伤害，含 ap/sap/he/torps/fire/flooding 等。
- `damage_by_type`：按伤害类型拆分的字典。
- `received_damage_details`：按弹种拆分的承受伤害。
- `hits_details`：按弹种拆分的命中数。
- `potential_damage_details`：潜在伤害拆分，含 artillery/torpedoes/planes。
- `damage_interactions`：以对手 db_id 为键的交互明细，可构建伤害关系图。

### 其他

| 字段 | 类型 | 说明 |
|---|---|---|
| time_lived_secs | number | 存活秒数 |
| survived | bool | 是否存活 |
| achievements | array | 成就列表 |
| ribbons | array | 勋带列表 |
| skill_info | object | 舰长技能点信息 |
| build | object | 舰船配置，含现代化改造、信号旗、消耗品、舰长技能 |
| consumables | array | 消耗品使用情况，含使用次数与总次数 |
| personal_rating | number/null | 个人评分，需额外传入预期值表 |
| connection | object/null | 连接信息 |
| observed_results | object | 客户端观测结果，含 damage 与 kills |

`observed_results` 与 `server_results` 可能有微小差异，应以 `server_results` 为准，它是服务器权威值。

## 字段到项目模型的映射

对齐 `PlayerRecord` 与 `BattleRecord`。

| replayshark 字段 | 项目模型字段 |
|---|---|
| metadata.timestamp | BattleRecord.StartedAt |
| metadata.played_duration | BattleRecord.DurationSeconds |
| metadata.map | BattleRecord.MapId |
| metadata.game_type | BattleRecord.BattleType |
| metadata.version | BattleRecord.GameVersion |
| metadata.battle_result.type | BattleRecord.Outcome |
| players[].db_id | PlayerRecord.AccountId |
| players[].name | PlayerRecord.PlayerName |
| players[].realm | PlayerRecord.Server |
| players[].clan | PlayerRecord.ClanTag |
| players[].team_id 对比 recorder team | PlayerRecord.IsAlly |
| players[].is_bot | PlayerRecord.IsBot |
| players[].is_hidden_profile | PlayerRecord.IsHidden |
| players[].ship_index | PlayerRecord.ShipId |
| players[].ship_tier | PlayerRecord.ShipTier |
| players[].ship_class | PlayerRecord.ShipClass |
| players[].server_results.damage | 战后伤害 |
| players[].server_results.kills | 战后击杀 |
| players[].server_results.spotting_damage | 点亮伤害 |
| players[].server_results.potential_damage | 潜在伤害 |
| players[].time_lived_secs | 存活秒数 |
| players[].survived | 存活与否 |
| players[].server_results.xp | 经验 |

`BattleType` 的 `game_type` 映射：

| 游戏值 | 枚举 |
|---|---|
| RandomBattle | Random |
| RankedBattle | Ranked |
| CoopBattle | Coop |
| ClanBattle | Clan |

`metadata.version` 是对象 `{major, minor, patch, build}`，拼接为 `major.minor.patch` 存入 `GameVersion`。

## 注意事项

**友敌判定。** 用 `players[].team_id` 与 `metadata.battle_result.team_id` 比较。相等为友，不等为敌。不要依赖 `relation`，它是录制者视角且可能因模式而异。

**组队规模。** 通过相同且非 null 的 `division_id` 分组，组内人数即组队规模。`division_id` 为 null 时是单排。

**未完成对局。** `played_duration` 为 0 或 `battle_result` 缺失时，该回放不含完整结果，应跳过入库，避免污染训练数据。

**船名依赖翻译文件。** `ship_name` 来自 `translations/<lang>/LC_MESSAGES/global.mo`。缺失时 `ship_name` 会退化成 ship_id 数字。所需文件是 `common/` 下的内容寻址对象，由 metadata.toml 中 `translations/en/LC_MESSAGES/global.mo` 等条目指向。

**字段体积。** 单个玩家的 `build` 与 `damage_interactions` 体积很大。入库时 `raw_json` 只保留必要字段，不要整块存，否则本地库膨胀很快。
