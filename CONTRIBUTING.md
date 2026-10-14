# 贡献指南

## 环境要求

| 组件 | 版本 | 用途 |
|---|---|---|
| .NET SDK | 10.0 | 客户端与 C# 库 |
| Python | 3.11+ | 模型管线 |
| uv | 最新 | Python 依赖管理 |
| pnpm | 11+ | 边缘后端依赖 |
| Node.js | 20+ | 边缘后端构建 |

## 快速开始

### C# 侧

```pwsh
dotnet restore WowsBattleIntel.slnx
dotnet build WowsBattleIntel.slnx -c Debug
dotnet test WowsBattleIntel.slnx -c Debug
```

### Python 侧

```pwsh
cd src/ml
uv venv .venv
uv pip install --python .venv -e ".[dev]"
.venv\Scripts\ruff.exe check src tests
.venv\Scripts\python.exe -m pytest -q
```

### 边缘后端

```pwsh
cd src/edge
pnpm install
pnpm typecheck
pnpm test
```

## 本地数据

游戏数据、工具二进制、解析产物、本地库都在 `data/`，不入库。结构说明与重建方式见 `data/README.md`。

首次运行前需要准备：

1. 从 `landaire/wows-toolkit` release 下载工具包，解压到 `data/tools/`。
2. 用 `tools/replay-inspection/fetch-minimal-gamedata.ps1` 拉取所需版本的游戏数据到 `data/gamedata/`。
3. 从 `landaire/wows-replay-data` 下载对应版本的 `constants.json` 到 `data/constants/`。

## 代码约定

### 通用

- 提交信息用中文，格式 `类型(范围): 简短描述`，类型取 `feat`、`fix`、`docs`、`test`、`chore`、`refactor`。
- 变更按逻辑分步提交，一次提交做一件事。
- 注释解释为什么这么做，不复述代码在做什么。
- 不写空洞的注释，如 `// 设置变量`。

### C#

- 语言版本与目标框架由 `Directory.Build.props` 统一控制，不在单个项目里覆盖。
- 包版本集中在 `Directory.Packages.props`，`csproj` 里不写版本号。
- 警告即错误，构建必须零警告。
- 未使用的 using 会报错，提交前清理。
- 公开类型与成员写 XML 文档注释，说明用途与设计意图。
- 领域模型保持不可变语义优先，需要可变时给出理由。

### Python

- 用 ruff 检查，行宽 100。
- 类型标注尽量完整。
- 特征相关的改动必须同步更新 C# 端的 `FeatureVectorBuilder`，反之亦然。

### TypeScript

- 严格模式，含 `noUncheckedIndexedAccess`、`exactOptionalPropertyTypes`。
- 不用 `any`，无法确定时用 `unknown` 再收窄。
- 导入路径带 `.js` 后缀，符合 ESM 规范。

## 关键契约

以下是不变量，改动需格外谨慎，必须同步更新两端或相关测试。

### 特征顺序

`FeatureVectorBuilder.FeatureNames` 与 Python 端 `MatchFeatures.feature_names()` 必须完全一致。顺序即模型输入顺序，错位会导致推理结果无意义。C# 侧有单元测试强制校验。

### 加权胜率口径

沿用 ApeRadar 的 `CalcWeightedWinrate`：单人场次权重 5、双排 2、三排 1，单船数据最多占两成权重。这是兼容契约，与现有工具对照时需要保持一致。

### 水平阈值

低于 47% 为猴，高于 58% 且场次超 500 为海王。可配置但默认值保持稳定。

### 时间泄漏防护

数据集构造默认只使用对局开始时间之前的玩家快照。任何绕过此项的开关都必须在文档与界面中显式标注，不得静默放宽。

### 负数哨兵

统计字段用 `-1` 表示未知，不要改成 0，否则与真实的零胜率、零场次混淆。

## 测试要求

- 新功能配单元测试，覆盖正常路径与边界条件。
- 解析、映射、算法类代码测试覆盖率应有保障。
- 涉及真实数据的验证写在 `tools/` 下的脚本，单元测试用合成数据，不含真实玩家信息。

## 隐私

- 不提交真实回放文件、聊天记录、账号 ID 明细。
- 测试数据用合成或脱敏值。
- 上传中央库的数据必须匿名化，原始回放默认不上传。
