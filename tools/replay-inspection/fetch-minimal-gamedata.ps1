<#
.SYNOPSIS
    从 landaire/wows-replay-data 精确拉取指定版本解析所需的最小文件集。

.DESCRIPTION
    replayshark 通过 metadata.toml 读取内容寻址存储。整仓 8GB 但单版本
    解析只需 scripts 定义与 GameParams 等少量对象，本脚本按 metadata.toml
    的文件映射逐个下载，重建出可用的 extracted 数据目录。

.PARAMETER Version
    版本目录名，例如 15.6.0_12830008。

.PARAMETER DestDir
    输出根目录。脚本会在其下建立 15.6.0_12830008 与 common。

.PARAMETER Scope
    下载范围。scripts 只拉脚本定义，all 拉除 gui 外全部。
#>

[CmdletBinding()]
param(
    [string]$Version = "15.6.0_12830008",
    [string]$DestDir = "C:\Windows\Temp\opencode\wows-extracted",
    [ValidateSet("scripts", "all")]
    [string]$Scope = "scripts"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$env:HTTP_PROXY = "http://127.0.0.1:50033"
$env:HTTPS_PROXY = "http://127.0.0.1:50033"

$rawBase = "https://raw.githubusercontent.com/landaire/wows-replay-data/main"
$versionDir = Join-Path $DestDir $Version
$commonDir = Join-Path $DestDir "common"

New-Item -ItemType Directory -Path $versionDir -Force | Out-Null
New-Item -ItemType Directory -Path $commonDir -Force | Out-Null

# --- 读取 metadata.toml 的文件映射 ---------------------------------------

$metaSrc = Join-Path $versionDir "metadata.toml"
if (-not (Test-Path $metaSrc)) {
    Write-Host "下载 metadata.toml"
    curl.exe -L -s -o $metaSrc "$rawBase/$Version/metadata.toml"
}

$metaLines = Get-Content $metaSrc
$entries = @()
foreach ($line in $metaLines) {
    if ($line -match '^"([^"]+)"\s*=\s*"([0-9a-f]{20})"') {
        $entries += [pscustomobject]@{ Path = $matches[1]; Hash = $matches[2] }
    }
}

Write-Host "metadata.toml 文件条目: $($entries.Count)"

# --- 按范围筛选 -----------------------------------------------------------

$needed = if ($Scope -eq "scripts") {
    $entries | Where-Object { $_.Path -like "scripts/*" -or $_.Path -like "content/*" }
} else {
    $entries | Where-Object { $_.Path -notlike "gui/*" }
}

Write-Host "本次需要下载: $($needed.Count) 个对象"

# --- 下载内容寻址对象 -----------------------------------------------------

$downloaded = 0
$skipped = 0
$failed = @()

foreach ($entry in $needed) {
    $hash = $entry.Hash
    $sub = $hash.Substring(0, 2)
    $rest = $hash.Substring(2)
    $subDir = Join-Path $commonDir $sub
    $objPath = Join-Path $subDir $rest
    New-Item -ItemType Directory -Path $subDir -Force | Out-Null

    if (Test-Path $objPath) {
        $skipped++
        continue
    }

    $url = "$rawBase/common/$sub/$rest"
    curl.exe -L -s -f -o $objPath $url 2>$null

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $objPath)) {
        $failed += $entry.Path
        continue
    }

    $downloaded++
    if ($downloaded % 10 -eq 0) {
        Write-Host "  已下载 $downloaded / $($needed.Count)"
    }
}

Write-Host "下载完成: 新增 $downloaded, 已存在 $skipped, 失败 $($failed.Count)"
if ($failed.Count -gt 0) {
    Write-Host "失败清单:"
    $failed | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
}

# --- 重建 vfs 可视化树 ----------------------------------------------------
# replayshark 通过 metadata.toml 解析，vfs 树仅供人工浏览，用符号链接重建。

$vfsDir = Join-Path $versionDir "vfs"
New-Item -ItemType Directory -Path $vfsDir -Force | Out-Null

$linked = 0
foreach ($entry in $needed) {
    $hash = $entry.Hash
    $sub = $hash.Substring(0, 2)
    $rest = $hash.Substring(2)
    $target = Join-Path $commonDir "$sub\$rest"
    $linkPath = Join-Path $vfsDir ($entry.Path -replace '/', '\')
    $linkParent = Split-Path $linkPath -Parent
    New-Item -ItemType Directory -Path $linkParent -Force | Out-Null

    if (Test-Path $linkPath) { continue }

    if (Test-Path $target) {
        try {
            New-Item -ItemType SymbolicLink -Path $linkPath -Target $target -ErrorAction Stop | Out-Null
            $linked++
        }
        catch {
            # 无符号链接权限时退化为复制。
            Copy-Item $target $linkPath -Force
            $linked++
        }
    }
}

Write-Host "vfs 树重建: $linked 个条目"

$totalMb = (Get-ChildItem $DestDir -Recurse -File -ErrorAction SilentlyContinue |
    Measure-Object Length -Sum).Sum / 1MB
Write-Host ("输出目录总大小: {0:N1} MB" -f $totalMb)
Write-Host "extracted 根目录: $DestDir"
