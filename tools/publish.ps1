<#
.SYNOPSIS
一键发布脚本：产出绿色免安装的分发包。

.DESCRIPTION
发布 WPF 客户端与 CLI 工具，按 ApeRadar 风格布局打包成 zip：
根目录主 exe，原生 DLL，models 目录放 ONNX 模型，README 与 config 模板。
产物为框架依赖单文件（需要用户已安装 .NET 10 桌面运行时），
体积约 15 MB，自包含方案约 148 MB 不采用。

.EXAMPLE
pwsh tools/publish.ps1
pwsh tools/publish.ps1 -Version 0.2.0 -SkipCli
#>

[CmdletBinding()]
param(
    # 版本号，默认取 git describe，无标签时回退到构建日期。
    [string]$Version,

    # 产物输出目录。
    [string]$OutputDir = "dist/publish",

    # 跳过 CLI 工具发布。
    [switch]$SkipCli,

    # 打包后启动客户端做冒烟验证，5 秒后结束进程。
    [switch]$SmokeTest
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

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

    $zipName = "wows-battle-intel-$Version-win-x64.zip"
    $stage = Join-Path $OutputDir "wows-battle-intel-$Version-win-x64"
    $zipPath = Join-Path $OutputDir $zipName

    Write-Host "[publish] 版本 $Version，输出 $OutputDir"

    # 幂等：先清后建。
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    New-Item $stage -ItemType Directory -Force | Out-Null

    $publishArgs = @(
        "publish", "src/client/WowsBattleIntel.Client/WowsBattleIntel.Client.csproj",
        "-c", "Release", "-r", "win-x64",
        "-p:PublishSingleFile=true", "-p:SelfContained=false", "-p:PublishTrimmed=false"
    )
    Write-Host "[publish] 发布客户端..."
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "客户端发布失败，退出码 $LASTEXITCODE" }

    $clientOut = Join-Path $repoRoot "src/client/WowsBattleIntel.Client/bin/Release/net10.0-windows/win-x64/publish"
    if (-not (Test-Path (Join-Path $clientOut "WowsBattleIntel.Client.exe"))) {
        throw "客户端发布产物缺失: $clientOut"
    }

    # 主 exe 与原生 DLL，排除调试符号、文档与静态链接库文件。
    Get-ChildItem $clientOut -File |
        Where-Object { $_.Extension -notin ".pdb", ".xml", ".lib" } |
        ForEach-Object { Copy-Item $_.FullName $stage }

    if (-not $SkipCli) {
        Write-Host "[publish] 发布 CLI..."
        dotnet publish src/client/WowsBattleIntel.Cli/WowsBattleIntel.Cli.csproj `
            -c Release -r win-x64 `
            -p:PublishSingleFile=true -p:SelfContained=false `
            -o (Join-Path $OutputDir "cli-tmp")
        if ($LASTEXITCODE -ne 0) { throw "CLI 发布失败，退出码 $LASTEXITCODE" }

        $cliOut = Join-Path $OutputDir "cli-tmp"
        Copy-Item (Join-Path $cliOut "wows-intel.exe") $stage
        Remove-Item $cliOut -Recurse -Force
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

    # config 模板与说明文档。
    Copy-Item (Join-Path $repoRoot "tools/publish/config.sample.json") $stage
    Copy-Item (Join-Path $repoRoot "tools/publish/README.txt") $stage

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

    Write-Host "[publish] 打 zip..."
    Compress-Archive -Path "$stage/*" -DestinationPath $zipPath -CompressionLevel Optimal

    $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "[publish] 完成: $zipPath ($zipMb MB)"
}
finally {
    Pop-Location
}
