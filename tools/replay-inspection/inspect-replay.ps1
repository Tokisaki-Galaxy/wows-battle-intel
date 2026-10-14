<#
.SYNOPSIS
    用 replayshark 解析一份回放，输出结构与统计摘要。

.DESCRIPTION
    阶段一的验证脚本。调用 replayshark dump 把回放转成 JSON lines，
    然后统计包数、提取元数据里的关键字段，帮助确认解析链路可用。

    脚本不修改任何原始文件，只在临时目录产出中间结果。

.PARAMETER ReplayPath
    回放文件路径。

.PARAMETER ReplaysharkPath
    replayshark 可执行文件路径。默认在 PATH 中查找。

.PARAMETER OutputDir
    中间产物输出目录。默认为系统临时目录。

.EXAMPLE
    ./inspect-replay.ps1 -ReplayPath "D:\...\20260722_132902_...Gold_harbor.wowsreplay"

.NOTES
    回放文件含真实账号信息。输出的样例脱敏后再分享。
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReplayPath,

    [Parameter(Mandatory = $false)]
    [string]$ReplaysharkPath = "replayshark",

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = (Join-Path $env:TEMP "wows-battle-intel")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- 前置检查 ---------------------------------------------------------------

if (-not (Test-Path -LiteralPath $ReplayPath)) {
    Write-Error "回放文件不存在: $ReplayPath"
    exit 1
}

$replay = Get-Item -LiteralPath $ReplayPath
Write-Host "回放文件: $($replay.Name)"
Write-Host "大小: $([math]::Round($replay.Length / 1MB, 2)) MB"
Write-Host "修改时间: $($replay.LastWriteTime)"

$shark = Get-Command $ReplaysharkPath -ErrorAction SilentlyContinue
if (-not $shark) {
    Write-Error "找不到 replayshark: $ReplaysharkPath。请先下载并放置到 PATH，或用 -ReplaysharkPath 指定完整路径。"
    exit 1
}
Write-Host "replayshark: $($shark.Source)"

# --- 准备输出目录 -----------------------------------------------------------

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$baseName = [System.IO.Path]::GetFileNameWithoutExtension($replay.Name)
$jsonlPath = Join-Path $OutputDir "$baseName.jl"

Write-Host ""
Write-Host "正在解析..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()

# replayshark dump 把结果写到标准输出。
& $shark.Source dump $replay.FullName 2>&1 | Set-Content -LiteralPath $jsonlPath -Encoding UTF8

$sw.Stop()
Write-Host "解析完成，耗时 $([math]::Round($sw.Elapsed.TotalSeconds, 2)) 秒"
Write-Host "输出: $jsonlPath"

# --- 统计摘要 ---------------------------------------------------------------

$lines = Get-Content -LiteralPath $jsonlPath
$totalLines = $lines.Count
Write-Host ""
Write-Host "JSON lines 总数: $totalLines"

if ($totalLines -eq 0) {
    Write-Error "解析输出为空，可能版本不匹配或文件损坏。"
    exit 1
}

# 第一行是元数据。
$meta = $lines[0] | ConvertFrom-Json
Write-Host ""
Write-Host "=== 元数据字段 ==="
$meta.PSObject.Properties | ForEach-Object {
    $value = $_.Value
    if ($value -is [array]) {
        Write-Host "  $($_.Name): 数组，$($value.Count) 项"
    }
    else {
        Write-Host "  $($_.Name): $value"
    }
}

# 如有玩家列表，统计人数。
if ($meta.PSObject.Properties.Name -contains "vehicles") {
    $vehicleCount = $meta.vehicles.Count
    Write-Host ""
    Write-Host "参战者数量: $vehicleCount"
}

Write-Host ""
Write-Host "=== 完成 ==="
Write-Host "下一步: 检查 $jsonlPath 的包结构，确认是否含战后结果字段。"
