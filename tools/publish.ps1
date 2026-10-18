<#
.SYNOPSIS
一键发布脚本：产出绿色免安装的分发包。

.DESCRIPTION
发布 WPF 客户端与 CLI 工具，按 ApeRadar 风格布局打包成 zip：
根目录主 exe，原生 DLL，models 目录放 ONNX 模型，README 与 config 模板。
产物为框架依赖单文件（需要用户已安装 .NET 10 桌面运行时），
原生库由 SDK 内嵌进单文件包，总体积约 4 MB，自包含方案约 148 MB 不采用。

产出两类分发物：
- 普通绿色 zip：解压即用，不支持自更新。
- Velopack 包（-SkipVelopack 关闭时）：Portable.zip + nupkg + releases.win.json，
  客户端可自动更新，上传 GitHub Releases 后生效。见 docs/decisions/0007-auto-update.md。

.EXAMPLE
pwsh tools/publish.ps1
pwsh tools/publish.ps1 -Version 0.2.0 -SkipCli
#>

[CmdletBinding()]
param(
    # 版本号，默认取 git describe，无标签时回退到构建日期。
    # Velopack 要求三段 semver，非三段版本会被自动规整。
    [string]$Version,

    # 产物输出目录。
    [string]$OutputDir = "dist/publish",

    # 跳过 CLI 工具发布。
    [switch]$SkipCli,

    # 跳过 Velopack 打包，只出普通 zip。
    [switch]$SkipVelopack,

    # 打包后启动客户端做冒烟验证，5 秒后结束进程。
    [switch]$SmokeTest
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function ConvertTo-Semver {
    <#
    .SYNOPSIS
    把任意版本串规整为 Velopack 接受的三段 semver。
    #>
    param([string]$Raw)

    # 0.2.0 这类已是三段的直接放行。
    if ($Raw -match '^\d+\.\d+\.\d+$') { return $Raw }

    # git describe 带提交数的：v0.2.0-3-gabcdef -> 0.2.0（放弃提交数，保证递增靠 tag）。
    if ($Raw -match '^(\d+)\.(\d+)\.(\d+)') { return "$($Matches[1]).$($Matches[2]).$($Matches[3])" }

    # 八位日期：20261015 -> 2026.10.15。
    if ($Raw -match '^(\d{4})(\d{2})(\d{2})$') { return "$($Matches[1]).$($Matches[2]).$($Matches[3])" }

    throw "无法规整版本号为三段 semver: $Raw（请用 -Version 传入 1.2.3 形式）"
}

# 立即退出并返回非零码，供 CI 与外层脚本判断。
trap {
    Write-Host "[publish] 失败: $_" -ForegroundColor Red
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    if (-not $Version) {
        $described = git describe --tags --always 2>$null
        if ($LASTEXITCODE -eq 0 -and $described) {
            $Version = "$described".TrimStart("v")
        }
        else {
            $Version = Get-Date -Format "yyyyMMdd"
        }
    }

    # 无标签仓库 git describe --always 退化为短哈希（如 af27fff），规整不出
    # semver。能规整才用，否则回退日期，保证任何仓库状态下脚本都能跑。
    $semver = try { ConvertTo-Semver $Version } catch { $null }
    if (-not $semver) {
        $Version = Get-Date -Format "yyyyMMdd"
        $semver = ConvertTo-Semver $Version
    }

    # Velopack 只认三段 semver。git describe 的 v0.2.0-3-gabcdef 截为主版本，
    $stage = Join-Path $OutputDir "wows-battle-intel-$Version-win-x64"

    Write-Host "[publish] 版本 $Version，输出 $OutputDir"

    # 幂等：先清后建。目录本身是 vpk 的输入，必须保留。
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item $stage -ItemType Directory -Force | Out-Null

    # 单文件发布，含原生库。IncludeNativeLibrariesForSelfExtract 必须显式打开，
    # 否则 onnxruntime 与 e_sqlite3 会被静默丢掉：包能启动，一碰数据库就崩。
    # 代价是原生库在首次启动时解压到 %TEMP%\.net\<程序名>。
    $publishArgs = @(
        "publish", "src/client/WowsBattleIntel.Client/WowsBattleIntel.Client.csproj",
        "-c", "Release", "-r", "win-x64",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:SelfContained=false", "-p:PublishTrimmed=false"
    )
    Write-Host "[publish] 发布客户端..."
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "客户端发布失败，退出码 $LASTEXITCODE" }

    $clientOut = Join-Path $repoRoot "src/client/WowsBattleIntel.Client/bin/Release/net10.0-windows/win-x64/publish"
    if (-not (Test-Path (Join-Path $clientOut "WowsBattleIntel.Client.exe"))) {
        throw "客户端发布产物缺失: $clientOut"
    }

    # 单文件产物只有一个 exe，pdb 与 xml 不进包。
    Get-ChildItem $clientOut -File |
        Where-Object { $_.Extension -notin ".pdb", ".xml", ".lib" } |
        ForEach-Object { Copy-Item $_.FullName $stage }

    if (-not $SkipCli) {
        # CLI 单独出一个包。它与客户端各自内嵌一份 onnxruntime 与 SQLite，
        # 合在一个包里等于把 11 MB 的 ONNX 库存两遍，普通用户用不到 CLI，
        # 分开分发能让主包小一半。
        Write-Host "[publish] 发布 CLI..."
        $cliStage = Join-Path $OutputDir "wows-intel-$Version-win-x64"
        $cliZip = Join-Path $OutputDir "wows-intel-$Version-win-x64-cli.zip"

        if (Test-Path $cliStage) { Remove-Item $cliStage -Recurse -Force }
        if (Test-Path $cliZip) { Remove-Item $cliZip -Force }
        New-Item $cliStage -ItemType Directory -Force | Out-Null

        dotnet publish src/client/WowsBattleIntel.Cli/WowsBattleIntel.Cli.csproj `
            -c Release -r win-x64 `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:SelfContained=false `
            -o (Join-Path $OutputDir "cli-tmp")
        if ($LASTEXITCODE -ne 0) { throw "CLI 发布失败，退出码 $LASTEXITCODE" }

        $cliOut = Join-Path $OutputDir "cli-tmp"
        Copy-Item (Join-Path $cliOut "wows-intel.exe") $cliStage
        Copy-Item (Join-Path $repoRoot "tools/publish/CLI-README.txt") $cliStage
        Remove-Item $cliOut -Recurse -Force

        Compress-Archive -Path "$cliStage/*" -DestinationPath $cliZip -CompressionLevel Optimal
        $cliZipMb = [math]::Round((Get-Item $cliZip).Length / 1MB, 1)
        Write-Host "[publish] CLI 包: $cliZip ($cliZipMb MB)"
    }

    # ONNX 模型随包分发，客户端默认探测 包目录/models/baseline.onnx。
    $modelSource = Join-Path $repoRoot "data/models/baseline.onnx"
    if (Test-Path $modelSource) {
        New-Item (Join-Path $stage "models") -ItemType Directory -Force | Out-Null
        Copy-Item $modelSource (Join-Path $stage "models/baseline.onnx")
    }
    else {
        Write-Host "[publish] 警告: 本地模型不存在，包内不含 models 目录" -ForegroundColor Yellow
    }

    # 只随包分发 replayshark 本身。解析所需的游戏定义与 GameParams 由
    # replayshark 的 -g 参数直接从游戏安装目录的 res_packages 里读，
    # 不再打包预提取的 gamedata 与 constants（那批数据曾让包体达到 150 MB）。
    #
    # 本地 data/tools 不存在时（典型场景：CI checkout，data/** 被
    # gitignore 排除）从上游 wows-toolkit release 下载。缺了它用户装好
    # 就无法解析回放，报"必须指定 replayshark 可执行文件路径"。
    $toolsSource = Join-Path $repoRoot "data/tools/replayshark.exe"
    if (-not (Test-Path $toolsSource)) {
        Write-Host "[publish] 本地无 replayshark，从上游 wows-toolkit release 下载..."
        $toolkitZip = Join-Path $env:RUNNER_TEMP "wows_toolkit_tools.zip"
        Invoke-WebRequest `
            -Uri "https://github.com/landaire/wows-toolkit/releases/latest/download/wows_toolkit_tools_win64.zip" `
            -OutFile $toolkitZip
        $toolkitExtract = Join-Path $env:RUNNER_TEMP "wows_toolkit"
        if (Test-Path $toolkitExtract) { Remove-Item $toolkitExtract -Recurse -Force }
        Expand-Archive -Path $toolkitZip -DestinationPath $toolkitExtract
        $shark = Get-ChildItem $toolkitExtract -Recurse -Filter "replayshark.exe" | Select-Object -First 1
        if ($shark) {
            New-Item (Join-Path $repoRoot "data/tools") -ItemType Directory -Force | Out-Null
            Copy-Item $shark.FullName $toolsSource
            Write-Host "[publish] replayshark 已就位: $($shark.FullName)"
        }
        else {
            Write-Host "[publish] 警告: 上游 release 里找不到 replayshark.exe" -ForegroundColor Yellow
        }
    }

    if (Test-Path $toolsSource) {
        New-Item (Join-Path $stage "tools") -ItemType Directory -Force | Out-Null
        Copy-Item $toolsSource (Join-Path $stage "tools/replayshark.exe")
    }
    else {
        Write-Host "[publish] 警告: replayshark 不存在，包内不含解析器，用户将无法解析回放" -ForegroundColor Yellow
    }

    # 关键文件自检把 replayshark 也纳入：缺失时直接失败而不是带病发布。
    # 上游下载失败或本地缺失都应阻断发布，让问题在 CI 暴露而非用户端。
    $required = @(
        "WowsBattleIntel.Client.exe",
        "models/baseline.onnx",
        "tools/replayshark.exe"
    )
    $missing = @($required | Where-Object { -not (Test-Path (Join-Path $stage $_)) })
    if ($missing.Count -gt 0) {
        throw "客户端包缺少关键文件: $($missing -join ', ')。"
    }

    $clientExeMb = (Get-Item (Join-Path $stage "WowsBattleIntel.Client.exe")).Length / 1MB
    if ($clientExeMb -lt 10) {
        throw ("客户端 exe 只有 {0:N1} MB，明显没含 onnxruntime 与 e_sqlite3。" +
            "请确认 publish 带了 -p:IncludeNativeLibrariesForSelfExtract=true。") -f $clientExeMb
    }

    # CLI 单独成包，同样要过体积检查。
    if (-not $SkipCli -and (Get-Variable -Name cliStage -Scope 0 -ErrorAction SilentlyContinue)) {
        $cliExeMb = (Get-Item (Join-Path $cliStage "wows-intel.exe")).Length / 1MB
        if ($cliExeMb -lt 10) {
            throw ("CLI exe 只有 {0:N1} MB，明显没含原生库。") -f $cliExeMb
        }
    }
    Write-Host ("[publish] 关键文件自检通过（客户端 exe {0:N1} MB）" -f $clientExeMb)

    if ($SmokeTest) {
        Write-Host "[publish] 冒烟验证客户端启动..."
        $proc = Start-Process (Join-Path $stage "WowsBattleIntel.Client.exe") `
            -WorkingDirectory $stage -PassThru
        Start-Sleep -Seconds 5
        if ($proc.HasExited) {
            throw "客户端启动即退出，退出码 $($proc.ExitCode)"
        }
        Stop-Process -Id $proc.Id -Force
        Write-Host "[publish] 冒烟通过: 进程存活 5 秒后正常结束"
    }

    # 不再额外打普通 zip。它的职责已由 Velopack 的 Portable.zip 覆盖，
    # 后者同样免安装解压即用，还支持自动更新。两种包并存只会让下载页
    # 多一个需要用户判断的选项。
    Write-Host "[publish] 客户端目录: $stage"

    if (-not $SkipVelopack) {
        Write-Host "[publish] Velopack 打包（semver $semver）..."
        if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
            throw "未找到 vpk 命令。请先执行: dotnet tool install -g vpk"
        }

        $vpkOut = Join-Path $OutputDir "velopack"
        if (Test-Path $vpkOut) { Remove-Item $vpkOut -Recurse -Force }

        # 输入就是现有 stage 目录，Velopack 只读不改。主 exe 名与 packId
        # 不同，--mainExe 不可省。
        vpk pack `
            --packId WowsBattleIntel `
            --packVersion $semver `
            --packDir $stage `
            --mainExe WowsBattleIntel.Client.exe `
            --exclude '.*\.pdb' `
            --outputDir $vpkOut
        if ($LASTEXITCODE -ne 0) { throw "vpk pack 失败，退出码 $LASTEXITCODE" }

        $portable = Get-ChildItem $vpkOut -Filter "*-Portable.zip" | Select-Object -First 1
        if (-not $portable) { throw "vpk 产物缺少 Portable.zip: $vpkOut" }

        $vpkMb = [math]::Round($portable.Length / 1MB, 1)
        Write-Host "[publish] Velopack 完成: $vpkOut ($vpkMb MB)"
        Write-Host "[publish] 发布到 GitHub Releases 时上传 $vpkOut 下全部 nupkg 与 releases.win.json，"
        Write-Host "[publish] Portable.zip 一并上传作为新用户下载物。命令: vpk upload github --publish ..."
    }
}
finally {
    Pop-Location
}
