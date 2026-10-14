<#
.SYNOPSIS
    批量解析回放目录，导出结构化战斗结果。

.DESCRIPTION
    对目录下所有回放调用 replayshark battle-results，逐个写出 JSON，
    并汇总统计。支持跳过已解析文件，支持进度落盘，便于中断续跑。

.PARAMETER ReplayDir
    回放所在目录。

.PARAMETER ExtractedDir
    游戏数据目录，通过 -e 传给 replayshark。

.PARAMETER ConstantsFile
    constants.json 路径，通过 -c 传入以避免联网。

.PARAMETER OutDir
    输出目录。

.PARAMETER Limit
    最多解析多少份，0 表示不限。
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReplayDir,
    [Parameter(Mandatory = $true)][string]$ExtractedDir,
    [Parameter(Mandatory = $true)][string]$ConstantsFile,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$Limit = 0,
    [string]$ReplaysharkPath = "C:\Windows\Temp\opencode\wows-toolkit\replayshark.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$replays = Get-ChildItem "$ReplayDir\*.wowsreplay" |
    Where-Object { $_.Name -ne "temp.wowsreplay" }
if ($Limit -gt 0) { $replays = $replays | Select-Object -First $Limit }

$total = $replays.Count
Write-Host "待解析: $total 份"

$ok = 0
$skipped = 0
$failed = @()
$i = 0
$sw = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($r in $replays) {
    $i++
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($r.Name)
    $expected = Join-Path $OutDir "$stem.json"

    if (Test-Path $expected) {
        $skipped++
        continue
    }

    & $ReplaysharkPath -e $ExtractedDir -c $ConstantsFile `
        battle-results --format normalized --out-dir $OutDir $r.FullName 2>$null | Out-Null

    if ($LASTEXITCODE -eq 0 -and (Test-Path $expected)) {
        $ok++
    }
    else {
        $failed += $r.Name
    }

    if ($i % 10 -eq 0) {
        $elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 0)
        $eta = if ($i -gt 0) { [math]::Round($sw.Elapsed.TotalSeconds / $i * ($total - $i), 0) } else { 0 }
        Write-Host "[$i/$total] 成功 $ok 跳过 $skipped 失败 $($failed.Count) 已用 ${elapsed}s ETA ${eta}s"
    }
}

Write-Host ""
Write-Host "解析完成: 成功 $ok, 跳过 $skipped, 失败 $($failed.Count)"
if ($failed.Count -gt 0) {
    Write-Host "失败清单:"
    $failed | ForEach-Object { Write-Host "  $_" }
}

# --- 汇总统计 -------------------------------------------------------------

$rows = @()
Get-ChildItem "$OutDir\*.json" -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        $j = Get-Content $_.FullName -Raw | ConvertFrom-Json
        $rows += [pscustomobject]@{
            文件   = $_.Name
            类型   = $j.metadata.game_type
            结果   = if ($j.metadata.battle_result) { $j.metadata.battle_result.type } else { "无" }
            人数   = $j.players.Count
            时长   = [int]$j.metadata.played_duration
            地图   = $j.metadata.map
        }
    }
    catch {
        Write-Host "汇总失败: $($_.Name)"
    }
}

Write-Host ""
Write-Host "=== 汇总 ==="
Write-Host "解析文件总数: $($rows.Count)"
Write-Host "有战斗结果的: $(($rows | Where-Object { $_.结果 -ne '无' }).Count)"
Write-Host ""
Write-Host "按结果:"
$rows | Group-Object 结果 | Select-Object Name, Count | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "按类型:"
$rows | Group-Object 类型 | Select-Object Name, Count | Format-Table -AutoSize | Out-String | Write-Host
