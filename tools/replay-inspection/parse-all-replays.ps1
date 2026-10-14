<#
.SYNOPSIS
    跨版本批量解析全部回放，支持多进程并发。

.DESCRIPTION
    扫描游戏回放根目录下的所有版本子目录，为每个版本匹配对应的
    extracted 游戏数据与 constants，调用 replayshark 解析。

    并发设计：每份回放相互独立，天然可并行。replayshark 每次调用要起
    一个进程并加载游戏定义，单进程时 CPU 大量空闲在 IO 与进程初始化上。
    用多进程并发能把总时间压到数分之一。

    并发度不宜拉满 CPU 核数：每个 replayshark 进程启动都要读游戏定义，
    进程过多会争抢磁盘 IO 反而变慢。经验值取核数的一半左右。

    与单版本脚本的差别：
    - 自动按目录名识别版本，不需要手工指定
    - 每个版本用各自的 extracted 目录与 constants，避免版本不匹配
    - 输出统一到一个目录，文件名带版本前缀避免重名覆盖
    - 记录解析失败的版本与原因，便于定位缺失的游戏数据
    - 支持跳过已解析文件，便于中断续跑

.PARAMETER ReplayRoot
    回放根目录，其下按版本分子目录。

.PARAMETER ExtractedRoot
    extracted 游戏数据根目录，其下按版本名分子目录。

.PARAMETER ConstantsRoot
    constants.json 所在的根目录，其下按版本名分子目录。

.PARAMETER OutDir
    输出目录。

.PARAMETER ThrottleLimit
    并发进程数。默认取逻辑核心数的一半，至少 1。

.EXAMPLE
    ./parse-all-replays.ps1 `
      -ReplayRoot "D:\SteamLibrary\steamapps\common\World of Warships\replays" `
      -ExtractedRoot "data\gamedata" `
      -ConstantsRoot "data\constants" `
      -OutDir "data\parsed" `
      -ThrottleLimit 4
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReplayRoot,
    [Parameter(Mandatory = $true)][string]$ExtractedRoot,
    [Parameter(Mandatory = $true)][string]$ConstantsRoot,
    [Parameter(Mandatory = $false)][string]$OutDir = "$PSScriptRoot\..\..\data\parsed",
    [string]$ReplaysharkPath = "$PSScriptRoot\..\..\data\tools\replayshark.exe",
    [int]$ThrottleLimit = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

if ($ThrottleLimit -le 0) {
    $cores = [Environment]::ProcessorCount
    $ThrottleLimit = [Math]::Max(1, [Math]::Floor($cores / 2))
}

# 解析为绝对路径，子进程运行时工作目录可能不同。
$OutDir = (New-Item -ItemType Directory -Path $OutDir -Force).FullName
$ReplaysharkPath = (Resolve-Path -LiteralPath $ReplaysharkPath).Path
$ExtractedRoot = (Resolve-Path -LiteralPath $ExtractedRoot).Path
$ConstantsRoot = (Resolve-Path -LiteralPath $ConstantsRoot).Path

Write-Host "并发度: $ThrottleLimit"

# --- 收集任务清单 ---------------------------------------------------------

$versionDirs = Get-ChildItem $ReplayRoot -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' }

Write-Host "发现 $($versionDirs.Count) 个版本目录"

$tasks = [System.Collections.Generic.List[object]]::new()
$missingData = [System.Collections.Generic.List[string]]::new()

foreach ($vdir in $versionDirs) {
    $versionName = $vdir.Name  # 形如 15.6.0.0

    # 找对应的 extracted 目录，目录名形如 15.6.0_12830008。
    $shortVersion = $versionName -replace '\.0$', ''  # 15.6.0.0 -> 15.6.0
    $extractedDir = Get-ChildItem $ExtractedRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "$shortVersion*" } |
        Select-Object -First 1

    if (-not $extractedDir) {
        Write-Host "[$versionName] 缺少 extracted 数据，跳过" -ForegroundColor Yellow
        $missingData.Add("$versionName (extracted)")
        continue
    }

    $constantsFile = Get-ChildItem $ConstantsRoot -Recurse -Filter "constants.json" -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like "*$($extractedDir.Name)" } |
        Select-Object -First 1

    if (-not $constantsFile) {
        Write-Host "[$versionName] 缺少 constants.json，跳过" -ForegroundColor Yellow
        $missingData.Add("$versionName (constants)")
        continue
    }

    $replays = Get-ChildItem "$($vdir.FullName)\*.wowsreplay" |
        Where-Object { $_.Name -ne "temp.wowsreplay" }

    Write-Host "[$versionName] $($replays.Count) 份回放, 数据=$($extractedDir.Name)"

    foreach ($r in $replays) {
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($r.Name)
        $expected = Join-Path $OutDir "$versionName`_$stem.json"

        # 已解析的跳过，支持中断续跑。
        if (Test-Path $expected) {
            continue
        }

        $tasks.Add([pscustomobject]@{
            ReplayPath    = $r.FullName
            Stem          = $stem
            ExpectedPath  = $expected
            ExtractedDir  = $extractedDir.FullName
            ConstantsFile = $constantsFile.FullName
            Version       = $versionName
        })
    }
}

Write-Host ""
Write-Host "待解析: $($tasks.Count) 份 (已存在的已跳过)"

if ($tasks.Count -eq 0) {
    Write-Host "没有需要解析的回放。"
    return
}

# --- 并发解析 -------------------------------------------------------------

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$results = $tasks | ForEach-Object -ThrottleLimit $ThrottleLimit -Parallel {
    $task = $_
    $shark = $using:ReplaysharkPath
    $outDir = $using:OutDir

    # replayshark 输出文件名由回放 stem 决定，不带版本前缀。
    $produced = Join-Path $outDir "$($task.Stem).json"

    # 同一目录下多进程写不同文件，但输出名可能与其他版本重名，
    # 因此用临时名中转，再由主进程重命名，避免并发冲突。
    $tempOut = Join-Path $outDir "tmp-$([Guid]::NewGuid().ToString('N')).json"

    $output = & $shark -e $task.ExtractedDir -c $task.ConstantsFile `
        battle-results --format normalized --out-file $tempOut $task.ReplayPath 2>&1

    if ($LASTEXITCODE -eq 0 -and (Test-Path $tempOut)) {
        Move-Item $tempOut $task.ExpectedPath -Force
        return [pscustomobject]@{ Ok = $true; Version = $task.Version; Error = $null }
    }

    if (Test-Path $tempOut) { Remove-Item $tempOut -Force }
    if (Test-Path $produced) { Remove-Item $produced -Force }

    return [pscustomobject]@{
        Ok = $false
        Version = $task.Version
        Error = ($output | Select-Object -Last 3) -join " "
    }
}

$sw.Stop()

$ok = @($results | Where-Object { $_.Ok }).Count
$failed = @($results | Where-Object { -not $_.Ok }).Count

# --- 汇总 -----------------------------------------------------------------

Write-Host ""
Write-Host "=== 解析完成 ==="
Write-Host "成功: $ok"
Write-Host "失败: $failed"
Write-Host "耗时: $([math]::Round($sw.Elapsed.TotalSeconds, 1)) 秒"

if ($failed -gt 0) {
    Write-Host ""
    Write-Host "失败样本:"
    $results | Where-Object { -not $_.Ok } | Select-Object -First 10 |
        ForEach-Object { Write-Host "  [$($_.Version)] $($_.Error)" }
}

if ($missingData.Count -gt 0) {
    Write-Host ""
    Write-Host "缺少数据的版本:"
    $missingData | ForEach-Object { Write-Host "  $_" }
}

$jsonCount = (Get-ChildItem "$OutDir\*.json" -ErrorAction SilentlyContinue).Count
Write-Host ""
Write-Host "输出目录 JSON 总数: $jsonCount"
