#Requires -Version 5.1
<#
.SYNOPSIS
  构建 WhiteBoard（M0 骨架版）。

.DESCRIPTION
  逐个工程构建，**不使用解决方案文件**。
  原因：本机 .NET SDK 10.0.400 安装不完整（缺少
  Microsoft.NET.SDK.WorkloadAutoImportPropsLocator 等 workload locator SDK 目录），
  导致"通过解决方案 restore"时静默失败（MSB4276）。单独构建工程不受影响。
  详见 documents\M0进展报告.md；修复 SDK 后可改回按解决方案构建。

.PARAMETER Publish
  构建成功后执行自包含发布到 publish\selfcontained，并生成 验证.cmd。

.PARAMETER Configuration
  构建配置，默认 Release。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$projects = @(
    'src\WhiteBoard.Core\WhiteBoard.Core.csproj',
    'src\WhiteBoard.Rendering\WhiteBoard.Rendering.csproj',
    'src\WhiteBoard.App\WhiteBoard.App.csproj',
    'tools\WbPeScan\WbPeScan.csproj',
    'poc\InkLatency\InkLatency.csproj',
    'tests\WhiteBoard.Rendering.Tests\WhiteBoard.Rendering.Tests.csproj',
    'tests\WhiteBoard.Core.Tests\WhiteBoard.Core.Tests.csproj'
)

foreach ($p in $projects) {
    Write-Host "-> 构建 $p" -ForegroundColor Cyan
    dotnet build $p -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host '   构建失败' -ForegroundColor Red; exit $LASTEXITCODE }
}

if ($Publish) {
    Write-Host '-> 自包含发布（免环境部署产物）' -ForegroundColor Cyan
    $out = Join-Path $root 'publish\selfcontained'
    dotnet publish (Join-Path $root 'src\WhiteBoard.App\WhiteBoard.App.csproj') `
        -c $Configuration -r win-x64 --self-contained true -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host '   发布失败' -ForegroundColor Red; exit $LASTEXITCODE }

    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Write-ValidationCmd -TargetDir $out

    $files = Get-ChildItem $out -Recurse -File
    $mb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host ("   产物：{0}   文件数 {1}   体积 {2} MB" -f $out, $files.Count, $mb) -ForegroundColor Green
    Write-Host '   双击 验证.cmd 可做自检；双击 WhiteBoard.exe 可打开界面。' -ForegroundColor DarkGray
}

Write-Host '-> 完成' -ForegroundColor Green

# 生成双击即可运行的自检批处理。
# 批处理由 cmd 按系统 ANSI 编码读取，因此含中文时必须用 ANSI(GBK) 写，否则中文乱码。
function Write-ValidationCmd {
    param([string]$TargetDir)

    $lines = @(
        '@echo off',
        'chcp 65001 >nul',
        'cd /d "%~dp0"',
        'echo ============================================================',
        'echo   WhiteBoard 自检（免环境部署验证）',
        'echo ============================================================',
        'echo.',
        '"%~dp0WhiteBoard.exe" --self-test --report "%~dp0selftest-report.txt"',
        'set RC=%ERRORLEVEL%',
        'echo.',
        'echo ------------------------------------------------------------',
        'echo  退出码：%RC%   (0 = 全部通过)',
        'echo  报告  ：%~dp0selftest-report.txt',
        'echo ------------------------------------------------------------',
        'echo.',
        'pause'
    )
    $text = ($lines -join "`r`n") + "`r`n"
    $ansi = [System.Text.Encoding]::GetEncoding(
        [System.Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage)
    [System.IO.File]::WriteAllText((Join-Path $TargetDir '验证.cmd'), $text, $ansi)
}
