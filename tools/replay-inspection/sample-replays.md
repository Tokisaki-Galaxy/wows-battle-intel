# 本机可用的测试回放

以下回放来自本地扫描，用于验证解析链路。回放文件本身不入库，只登记路径供参考。

## 15.6.0.0 版本

游戏版本较新，是解析验证的主要目标。

路径：`D:\SteamLibrary\steamapps\common\World of Warships\replays\15.6.0.0\`

样本（2026 年 7 月）：

| 文件名 | 大小 | 船 |
|---|---|---|
| 20260722_124308_...Cyber-Ruggiero...AngelWings | 1.8 MB | Cyber-Ruggiero-di-Lauria |
| 20260722_125831_...Archerfish_50_Gold_harbor | 2.4 MB | Archerfish |
| 20260722_132406_...Archerfish_35_NE_north_winter | 0.6 MB | Archerfish |
| 20260722_132902_...Archerfish_50_Gold_harbor | 2.5 MB | Archerfish |
| 20260722_134530_...Archerfish_53_Shoreside | 2.1 MB | Archerfish |
| 20260722_154336_...Max-Immelmann_15_NE_north | 2.9 MB | Max Immelmann |
| 20260723_092956_...Archerfish_00_CO_ocean | 2.6 MB | Archerfish |
| 20260724_152521_...Archerfish_40_Okinawa | 1.8 MB | Archerfish |

备选样本：`20260722_125741_...Cyber-Ruggiero...AngelWings`（59 KB，异常小，可能是中断的对局，可作健壮性测试）。

## 更早版本

可能因版本不匹配无法解析，作对照用。

| 路径 | 船 | 日期 |
|---|---|---|
| `Z:\Code\20251109_101103_...Schlieffen_38_Canada` | Schlieffen | 2025-11 |
| `D:\tokisaki\Downloads\20250117_...Halford_55_Seychelles` | Halford | 2025-01 |

## 验证步骤

1. 挑一份 15.6.0.0 的回放。
2. 运行 `replayshark dump` 输出 JSON lines。
3. 检查第一行元数据是否含完整的玩家列表。
4. 检查是否含战后结果：胜负、每人伤害、击杀、存活。
5. 记录字段结构，写进解析结构说明。

## 注意事项

- 回放文件含真实账号 ID 与昵称。验证输出的样例要脱敏后再入库，或只入库脚本不入库数据。
- `.gitignore` 已排除 `*.wowsreplay`，避免误提交大文件。
