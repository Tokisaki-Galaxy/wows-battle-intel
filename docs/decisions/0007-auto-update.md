# ADR 0007：WPF 客户端自动更新方案

- 状态：建议采纳
- 日期：2026-10-15

## 背景

客户端目前以绿色免安装 zip 分发：`tools/publish.ps1` 产出框架依赖单文件主 exe，onnxruntime.dll 等原生库由 .NET SDK 内嵌进单文件包，外加 models 目录、config 模板与 README。仓库即将转 public，正式分发渠道定为 GitHub Releases。

服务端 Cloudflare Worker 已有 `/api/v1/models` 端点覆盖 ONNX 模型版本查询，客户端本体的更新通道尚未建立。用户手工下载新 zip 替换目录的方式不可持续，需要一套自动更新机制，且必须兼容绿色免安装形态，不引入安装器，不改目录结构。

## 候选方案对比

| 维度 | Velopack | Squirrel.Windows | NetSparkle | 自研 |
|---|---|---|---|---|
| net10 兼容性 | 好，官方支持 .NET 5+，持续跟进新运行时 | 差，官方最后发布 2020 年，.NET 8 单文件与 SquirrelAware 检测有明确兼容问题 | 好，明确支持 .NET 6+，net10 可用 | 好，仅用 BCL 与 HttpClient |
| 绿色免安装包支持 | 原生支持，`vpk pack` 默认产出 `-Portable.zip`，解包即用且可自更新 | 不支持，强制安装器形态与 `app-1.0.0` 版本目录 | 好，zip 包下载后由开发者自行解压替换，目录完全自控 | 好，完全自控 |
| 增量更新 | 内建 delta nupkg，`vpk pack` 自动对上一版本生成差分包 | 内建，基于 RELEASES 与 nupkg 差量机制 | 有限，需 appcast 工具链生成二进制差分，配置繁琐 | 无，需自建差分或只做全量 |
| 回滚 | 应用期原子换目录，失败保留旧版；运行期无自动版本回退 | 有 `uninstall -v` 回退，但框架本身不可靠 | 有 Rollback 示例，依赖开发者自行实现替换与恢复 | 全部自行实现 |
| 维护活跃度 | 活跃，Squirrel 原作者 Clowd.Squirrel 的后继项目，C#/Rust/JS/C++/Python 多语言 SDK 持续更新 | 实质停滞，社区长期寻找替代品 | 中等，2025 年仍有 3.x 发布与提交，但几乎单一贡献者 | 取决于自己 |
| 包体积增量 | 约 1 MB 级，引入 Velopack 运行库与少量附加文件 | 数 MB | 小于 1 MB | 零 |

补充说明：

- Squirrel.Windows 已确认无 .NET 8+ 的正常支持路径，官方讨论明确建议另寻方案，直接排除。
- NetSparkle 是可行备选。它不干预目录形态，Ed25519 签名校验完善，但要求自建 appcast XML 工具链，增量与回滚都要自己拼装，集成成本接近自研的六成而收益有限。
- 自研的全部工作量集中在边界情况：断点续传、哈希校验、退出后辅助进程换文件、部分替换失败恢复、杀软误报处理。这些正是 Velopack 已经踩平的坑。

## 决策

采用 Velopack。客户端引用 `Velopack` NuGet 包，发布端用 `vpk` CLI 打包，更新源指向 GitHub Releases，用户分发物沿用绿色 zip 形态，即 `vpk` 产出的 `*-Portable.zip`。

### 目录形态兼容性评估

这是本次选型的核心关注点，结论：兼容，无需改目录结构。

- `vpk pack` 的输入 `--packDir` 接受任意 `dotnet publish` 产物目录。框架依赖单文件 exe（原生库内嵌）、models 子目录全部按原样进入更新包，Velopack 只做目录打包与差分，不重排文件。
- Velopack 不强制安装器形态。`vpk pack` 默认同时产出 `Setup.exe` 与 `Portable.zip`，两者来自同一套 nupkg 资产。我们只分发 `Portable.zip`，Setup 丢弃即可。
- 安装器形态下 Velopack 会把程序放 `{root}\current\` 并管理快捷方式；portable 形态解包即用，程序就在解压目录根，更新时由 Velopack 在应用目录旁维护 packages 目录并原子换文件，不迁移目录。
- 需要接受的两处小变化：解包产物里会多出 Velopack 自身的少量文件，如 `Velopack.dll` 与更新辅助文件；`Main` 入口最前面必须调用 `VelopackApp.Build().Run()`，用于处理更新钩子。这两点不影响原有目录布局逻辑，models 目录、config 探测路径均不变。
- 官方文档推荐自包含发布，但仅是推荐。`--framework` 选项明确支持框架依赖形态，用户需已装 .NET 10 桌面运行时的前提与现状一致。

### 版本号约束

Velopack 只接受三段 semver，不支持四段。`publish.ps1` 现有版本来源需要规整：`git describe` 产出的 `v0.2.0-3-gabcdef` 必须截断为 `0.2.0` 或映射为 `0.2.0-3`，日期回退的 `yyyyMMdd` 需映射为 `2026.10.15` 这类合法三段式。

## 更新流程设计

启动检查异步执行，绝不阻塞 UI。全流程四步：

```csharp
[STAThread]
private static void Main(string[] args)
{
    // 更新钩子必须最先执行
    VelopackApp.Build().Run();

    // 后台 fire-and-forget 检查，见下文
    _ = UpdateService.CheckAndPromptAsync();

    var app = new App();
    app.InitializeComponent();
    app.Run();
}
```

更新服务核心逻辑：

```csharp
var source = new GithubSource(
    "https://github.com/<owner>/wows-battle-intel",
    accessToken: null,   // 仓库转 public 后免 token，见下节
    prerelease: false);

var mgr = new UpdateManager(source);

// 1. 启动后延迟数秒再检查，避开启动竞态
var update = await mgr.CheckForUpdatesAsync();
if (update is null) return;

// 2. 下载，带进度回调供状态栏或气泡展示
await mgr.DownloadUpdatesAsync(update,
    p => progressReporter.Report(p));

// 3. 提示用户，允许推迟
if (userConfirmed)
{
    // 退出后由 Velopack 换文件再重启
    mgr.ApplyUpdatesAndRestart(update);
}
```

要点：

- 差量优先。`DownloadUpdatesAsync` 自动优先取 delta nupkg，取不到回退全量。delta 由发布端 `vpk download` 拉取上一版后自动生成。
- 应用时机用 `ApplyUpdatesAndRestart`，用户确认后由 Velopack 退出进程、原子换文件、重新拉起。用户拒绝则保留已下载的包，下次启动提示。
- 检查频率在客户端做节流，例如每 6 小时至多一次，结果落本地配置。GitHub 未认证 API 限流按 IP 计 60 次/时，单用户场景远够，节流只为礼貌。
- 失败回滚依赖 Velopack 的原子换目录语义：换文件中途失败，旧版目录保持完整可用。换文件成功但新版启动即崩的情况无自动版本回退，属于残余风险，靠发布前冒烟验证兜底，必要时可用 `vpk` 重新发布旧版本号把 feed 拉回。
- 模型文件更新与客户端本体更新是两条独立通道，模型走现有 `/api/v1/models`，互不干扰。

## GitHub Releases 集成

### 资产命名约定

每次发布上传以下资产到同一个 release，tag 为 `v{version}`：

```
WowsBattleIntel-{version}-full.nupkg      # 必需，全量更新包
WowsBattleIntel-{version}-delta.nupkg     # 可选，相对上一版的差分包
WowsBattleIntel-Portable.zip              # 必需，新用户下载物
releases.win.json                         # 必需，更新 feed，GithubSource 靠它发现版本
```

`Setup.exe`、`RELEASES`、`assets.win.json` 不上传。`releases.win.json` 与 nupkg 必须在同一层级，客户端通过 release 资产列表解析出 feed 地址。

### token 需求变化

- 仓库现为 private：`GithubSource` 的 `accessToken` 必须传 GitHub PAT，`vpk download github` 与 `vpk upload github` 也要加 `--token`，否则 feed 与资产都 404。
- 转为 public 后：客户端 `accessToken: null` 即可工作，无 token 的请求走匿名 API 限额。发布流水线仍要 `GITHUB_TOKEN`，那是写权限需求，与读取无关。
- 客户端代码从第一天就按「token 可空」设计，从本地 config 读可选的 `githubToken` 字段，private 期间让内测用户填，public 后留空。避免转 public 时改代码。

### 限流

- 元数据查询走 GitHub REST API，未认证限额 60 次/时/IP，认证后 5000 次/时。启动节流至 6 小时一次后，单用户每小时最多 4 次查询，安全。
- 资产下载走 `objects.githubusercontent.com` CDN，不受 API 限流约束，全量包 4 MB 级下载无压力。

## 发布脚本衔接

`tools/publish.ps1` 需追加一个 Velopack 打包段，置于现有 zip 产出之后。前置条件是全局装过 `dotnet tool install -g vpk`。追加步骤如下：

```powershell
# 1. 规整版本号为三段 semver（现有 $Version 来源需先做映射）
$v = ConvertTo-Semver $Version

# 2. 拉取线上最新版，供 vpk 自动生成 delta；仓库 private 期间带 --token
vpk download github --repoUrl <owner>/wows-battle-intel --token $env:GH_TOKEN

# 3. 打包：输入目录就是现有的 $stage，主 exe 名与 packId 不同所以必须给 --mainExe
vpk pack `
    --packId WowsBattleIntel `
    --packVersion $v `
    --packDir $stage `
    --mainExe WowsBattleIntel.Client.exe `
    --exclude '.*\.pdb' `
    --outputDir $OutputDir/velopack

# 4. 上传 release（CI 中执行；本地发布可手工传）
vpk upload github --repoUrl <owner>/wows-battle-intel `
    --token $env:GH_TOKEN --publish `
    --releaseName "WowsBattleIntel $v" --tag "v$v"
```

对现有脚本的影响：

- `$stage` 目录内容不变，`vpk pack` 只读不改。现有 `Compress-Archive` 产出的手工 zip 可继续作为过渡期分发物，与 Velopack 包并存。
- `--packId WowsBattleIntel` 与主 exe 名 `WowsBattleIntel.Client.exe` 不一致，`--mainExe` 参数不可省。
- SmokeTest 逻辑保留，建议冒烟对象换成 `vpk` 产出的 Portable.zip 解包目录，验证更新钩子不破坏启动。

## 代价

**引入运行时依赖。** 客户端多出约 1 MB 的 Velopack 运行库，解包目录多几个 Velopack 文件。README 的目录说明需同步更新。

**版本号纪律。** 三段 semver 强制约束发布流程，`publish.ps1` 的版本来源逻辑必须改造，CI 与本地发布的版本规整规则要保持一致。

**portable 自更新的形态验证。** Velopack 对 portable zip 的自更新是官方支持的路径。本项目「单文件 exe（原生库内嵌）」组合已于 2026-10-15 用真实包验证：vpk pack 产出 Portable.zip 解包后主程序于 current 目录正常启动，冒烟存活无崩溃。端到端更新链路（检查、下载、应用、重启）待首个 Release 上线后实测。若有意外，退路是 NetSparkle 或自研，目录形态完全不动。

**锁定单一上游。** 更新通道由 Velopack 托管，feed 格式与换文件机制绑定其实现。缓解是 feed 本质是静态 json 与 nupkg，极端情况下可手工构造。

## 参考

- Velopack 文档：打包概览与 GitHub Actions 部署，docs.velopack.io
- Squirrel.Windows .NET 8 兼容性官方讨论，Squirrel/Squirrel.Windows Discussion 1881
- NetSparkleUpdater 3.x，WPF UI 包 2025 年仍有发布
- 本仓库现有 ADR 位于 `docs/decisions/`，本文合入时改号为 0007
