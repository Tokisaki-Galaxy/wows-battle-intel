<#
.SYNOPSIS
    跨版本批量解析全部回放。

.DESCRIPTION
    扫描游戏回放根目录下的所有版本子目录，为每个版本匹配对应的
    extracted 游戏数据与 constants，调用 replayshark 解析。

    与单版本脚本的差别：
    - 自动按目录名识别版本，不需要手工指定
    - 每个版本用各自的 extracted 目录与 constants，避免版本不匹配
    - 输出统一到一个目录，文件名带版本前缀避免重名覆盖
    - 记录解析失败的版本与原因，便于定位缺失的游戏数据

.PARAMETER ReplayRoot
    回放根目录，其下按版本分子目录。

.PARAMETER ExtractedRoot
    extracted 游戏数据根目录，其下按版本名分子目录。

.PARAMETER ConstantsRoot
    constants.json 所在的根目录，其下按版本名分子目录。

.PARAMETER OutDir
    输出目录。

.EXAMPLE
    ./parse-all-replays.ps1 `
      -ReplayRoot "D:\SteamLibrary\steamapps\common\World of Warships\replays" `
      -ExtractedRoot "C:\Windows\Temp\opencode\wows-extracted" `
      -ConstantsRoot "C:\Windows\Temp\opencode\wows-replay-data" `
      -OutDir "C:\Windows\Temp\opencode\replay-out\all"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReplayRoot,
    [Parameter(Mandatory = $true)][string]$ExtractedRoot,
    [Parameter(Mandatory = $true)][string]$ConstantsRoot,
    [Parameter(Mandatory = $false)][string]$OutDir = "$PSScriptRoot\..\..\data\parsed",
    [string]$ReplaysharkPath = "$PSScriptRoot\..\..\data\tools\replayshark.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# --- 收集各版本回放 -------------------------------------------------------

$versionDirs = Get-ChildItem $ReplayRoot -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' }

Write-Host "发现 $($versionDirs.Count) 个版本目录"

$totalOk = 0
$totalSkipped = 0
$totalFailed = 0
$missingData = @()

foreach ($vdir in $versionDirs) {
    $versionName = $vdir.Name  # 形如 15.6.0.0

    # 找到对应的 extracted 目录。目录名形如 15.6.0_12830008。
    $shortVersion = $versionName -replace '\.0$', ''  # 15.6.0.0 -> 15.6.0
    $extractedDir = Get-ChildItem $ExtractedRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "$shortVersion*" } |
        Select-Object -First 1

    if (-not $extractedDir) {
        Write-Host "[$versionName] 缺少 extracted 数据，跳过" -ForegroundColor Yellow
        $missingData += "$versionName (extracted)"
        continue
    }

    # 找到对应的 constants。
    $constantsFile = Get-ChildItem $ConstantsRoot -Recurse -Filter "constants.json" -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like "*$($extractedDir.Name)" } |
        Select-Object -First 1

    if (-not $constantsFile) {
        Write-Host "[$versionName] 缺少 constants.json，跳过" -ForegroundColor Yellow
        $missingData += "$versionName (constants)"
        continue
    }

    $replays = Get-ChildItem "$($vdir.FullName)\*.wowsreplay" |
        Where-Object { $_.Name -ne "temp.wowsreplay" }

    Write-Host ""
    Write-Host "[$versionName] $($replays.Count) 份回放, 数据=$($extractedDir.Name)"

    $ok = 0
    $skipped = 0
    $failed = 0

    foreach ($r in $replays) {
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($r.Name)
        # 输出文件名加版本前缀，避免跨版本重名。
        $expected = Join-Path $OutDir "$versionName`_$stem.json"

        if (Test-Path $expected) {
            $skipped++
            continue
        }

        & $ReplaysharkPath -e $extractedDir.FullName -c $constantsFile.FullName `
            battle-results --format normalized --out-dir $OutDir $r.FullName 2>$null | Out-Null

        # replayshark 输出文件名由回放 stem 决定，不带版本前缀，需重命名。
        $produced = Join-Path $OutDir "$stem.json"
        if ($LASTEXITCODE -eq 0 -and (Test-Path $produced)) {
            Move-Item $produced $expected -Force
            $ok++
        }
        else {
            if (Test-Path $produced) { Remove-Item $produced -Force }
            $failed++
        }
    }

    Write-Host "  成功 $ok, 跳过 $skipped, 失败 $failed"
    $totalOk += $ok
    $totalSkipped += $skipped
    $totalFailed += $failed
}

# --- 汇总 -----------------------------------------------------------------

Write-Host ""
Write-Host "=== 全部完成 ==="
Write-Host "新增成功: $totalOk"
Write-Host "已存在跳过: $totalSkipped"
Write-Host "失败: $totalFailed"
if ($missingData.Count -gt 0) {
    Write-Host "缺少数据的版本:"
    $missingData | ForEach-Object { Write-Host "  $_" }
}

$jsonCount = (Get-ChildItem "$OutDir\*.json" -ErrorAction SilentlyContinue).Count
Write-Host "输出目录 JSON 总数: $jsonCount"
