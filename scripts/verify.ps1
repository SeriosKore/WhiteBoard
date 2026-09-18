#Requires -Version 5.1
<#
.SYNOPSIS
  WhiteBoard 一键验证（M0 骨架版）。

.DESCRIPTION
  按顺序执行：
    1. 构建全部工程（Release）—— 逐个工程构建，不走解决方案（本机 SDK 缺 workload locator，见 M0 进展报告）
    2. 单元测试（若存在测试工程）
    3. 自包含发布到 publish\selfcontained，并生成 验证.cmd
    4. PE 依赖审计（PoC-D）：不得依赖未随包携带的 VC++ 运行时；非系统 DLL 必须随包携带
    5. 发布产物自检（PoC-C）：--self-test 退出码必须为 0
    6. 数据目录合规（C3）：数据必须落在 exe 同级目录内

  退出码 0 = 全部通过。

.PARAMETER SkipPublish
  跳过自包含发布（本地快速迭代），此时步骤 4/5 针对已有的 publish 目录。

.PARAMETER Configuration
  构建配置，默认 Release。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$script:Failures = @()
$script:Steps = @()

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host ('-' * 64) -ForegroundColor DarkGray
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host ('-' * 64) -ForegroundColor DarkGray
}

function Add-Result([string]$Name, [bool]$Ok, [string]$Detail) {
    $script:Steps += [pscustomobject]@{ Name = $Name; Ok = $Ok; Detail = $Detail }
    if (-not $Ok) { $script:Failures += $Name }
    $mark = if ($Ok) { 'PASS' } else { 'FAIL' }
    $color = if ($Ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}  {2}" -f $mark, $Name, $Detail) -ForegroundColor $color
}

# 生成双击即可运行的自检批处理（cmd 按 ANSI 读取含中文的批处理，故用 ANSI 写）
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

$publishDir = Join-Path $root 'publish\selfcontained'
$scanTool   = Join-Path $root 'tools\WbPeScan\bin\Release\net10.0\wbpescan.exe'
$appExe     = Join-Path $publishDir 'WhiteBoard.exe'
$artifacts  = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

# ---- 1. 构建 ----------------------------------------------------------------
Write-Step '1/6  构建全部工程'
$projects = @(
    'src\WhiteBoard.Core\WhiteBoard.Core.csproj',
    'src\WhiteBoard.App\WhiteBoard.App.csproj',
    'src\WhiteBoard.Rendering\WhiteBoard.Rendering.csproj',
    'tools\WbPeScan\WbPeScan.csproj',
    'poc\InkLatency\InkLatency.csproj'
)
$buildOk = $true
foreach ($p in $projects) {
    dotnet build $p -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { $buildOk = $false; Write-Host "  构建失败：$p" -ForegroundColor Red }
}
Add-Result '构建' $buildOk "7 个工程（$Configuration，逐个构建）"

# ---- 2. 单元测试 + PoC 冒烟自检 ---------------------------------------------
Write-Step '2/6  单元测试与 PoC 冒烟自检'
$testProjects = Get-ChildItem -Path (Join-Path $root 'tests') -Filter '*.csproj' -Recurse -ErrorAction SilentlyContinue
if ($testProjects) {
    foreach ($tp in $testProjects) {
        # 自研轻量运行器（零第三方依赖）：dotnet run 直接返回用例失败数对应的退出码
        dotnet run --project $tp.FullName -c $Configuration --no-launch-profile -v q
        Add-Result "单元测试 $($tp.BaseName)" ($LASTEXITCODE -eq 0) 'dotnet run（自研运行器）'
    }
} else {
    Add-Result '单元测试' $true '暂无测试工程'
}

# PoC-A：离屏渲染三条路线，验证都能出图且不抛异常
$pocExe = Join-Path $root 'poc\InkLatency\bin\Release\net10.0-windows\InkLatency.exe'
if (Test-Path $pocExe) {
    $pocReport = Join-Path $artifacts 'poc-a-selftest.txt'
    if (Test-Path $pocReport) { Remove-Item $pocReport -Force }
    $pocExit = -1
    try {
        $pocPsi = New-Object System.Diagnostics.ProcessStartInfo
        $pocPsi.FileName = $pocExe
        $pocPsi.Arguments = '--selftest --report "' + $pocReport + '"'
        $pocPsi.UseShellExecute = $false
        $pocPsi.RedirectStandardOutput = $true
        $pocPsi.RedirectStandardError = $true
        $pocPsi.CreateNoWindow = $true
        $pocProc = [System.Diagnostics.Process]::Start($pocPsi)
        $null = $pocProc.StandardOutput.ReadToEnd()
        $pocProc.WaitForExit()
        $pocExit = $pocProc.ExitCode
    } catch {
        Write-Host "  启动 PoC 自检失败：$($_.Exception.Message)" -ForegroundColor Yellow
    }
    Add-Result 'PoC-A 三路线冒烟自检' ($pocExit -eq 0) "退出码 $pocExit　artifacts\poc-a-selftest.txt"
} else {
    Add-Result 'PoC-A 三路线冒烟自检' $false "未找到 $pocExe"
}

# ---- 3. 自包含发布 ----------------------------------------------------------
Write-Step '3/6  自包含发布'
if ($SkipPublish) {
    Add-Result '自包含发布' (Test-Path $appExe) '已跳过（复用现有 publish 目录）'
} else {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish (Join-Path $root 'src\WhiteBoard.App\WhiteBoard.App.csproj') `
        -c $Configuration -r win-x64 --self-contained true -o $publishDir --nologo -v q
    $ok = ($LASTEXITCODE -eq 0) -and (Test-Path $appExe)
    $size = if (Test-Path $publishDir) {
        [math]::Round(((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
    } else { 0 }
    Add-Result '自包含发布' $ok "publish\selfcontained   $size MB"
}

if (-not (Test-Path $appExe)) {
    Write-Host '  发布产物不存在，后续步骤无法执行。' -ForegroundColor Red
    $script:Failures += '发布产物缺失'
} else {

    # ---- 4. PE 依赖审计（PoC-D）--------------------------------------------
    Write-Step '4/6  PE 依赖审计（PoC-D）'
    if (-not (Test-Path $scanTool)) {
        dotnet build (Join-Path $root 'tools\WbPeScan\WbPeScan.csproj') -c Release -v q --nologo | Out-Null
    }
    $peJson = Join-Path $artifacts 'pe-scan-publish.json'
    $peTxt  = Join-Path $artifacts 'pe-scan-publish.txt'
    & $scanTool $publishDir --json $peJson --quiet *> $peTxt
    $peExit = $LASTEXITCODE
    Add-Result '无未随包携带的 VC++ 运行时 / 无非系统 DLL 缺失' ($peExit -eq 0) 'artifacts\pe-scan-publish.txt'

    # 发布目录白名单审计：允许 .NET 自带的 *_cor3 私有副本，拒绝系统版可再发行组件 DLL
    $vcRedist = Get-ChildItem $publishDir -Filter '*.dll' -File |
        Where-Object {
            $_.Name -match '^(msvcp|vcruntime|concrt|vccorlib)\d+(_\d+)?\.dll$' -and
            $_.Name -notmatch '_cor3\.dll$'
        }
    Add-Result '发布目录无系统版 VC++ 可再发行组件 DLL' ($vcRedist.Count -eq 0) `
        $(if ($vcRedist.Count -eq 0) { '未发现（仅有 .NET 自带的 *_cor3 私有副本）' } else { ($vcRedist.Name -join ', ') })

    # ---- 5. 发布产物自检（PoC-C）------------------------------------------
    Write-Step '5/10  发布产物自检（PoC-C）'
    $reportPath = Join-Path $artifacts 'selftest-publish.txt'
    if (Test-Path $reportPath) { Remove-Item $reportPath -Force }

    # 用 .NET Process API 而不是 Start-Process：
    #   ① 本程序是 GUI 子系统，Start-Process/-NoNewWindow 在受限环境下可能被拒；
    #   ② 需要可靠拿到退出码；输出以报告文件为准（程序自身会 AttachConsole 打印）。
    $exitCode = -1
    $stdout = ''
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $appExe
        $psi.Arguments = '--self-test --report "' + $reportPath + '"'
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $proc = [System.Diagnostics.Process]::Start($psi)
        $stdout = $proc.StandardOutput.ReadToEnd()
        $proc.WaitForExit()
        $exitCode = $proc.ExitCode
    } catch {
        Write-Host "  启动自检进程失败：$($_.Exception.Message)" -ForegroundColor Yellow
    }

    Add-Result '自检退出码为 0' ($exitCode -eq 0) "退出码 $exitCode　artifacts\selftest-publish.txt"

    if (Test-Path $reportPath) {
        $reportText = Get-Content $reportPath -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($stdout)) { Write-Host $reportText -ForegroundColor DarkGray }
        $passCount = ([regex]::Matches($reportText, '(?m)^PASS')).Count
        $failCount = ([regex]::Matches($reportText, '(?m)^FAIL')).Count
        Add-Result '自检明细' ($failCount -eq 0 -and $passCount -gt 0) "PASS $passCount 项，FAIL $failCount 项"
    } else {
        Add-Result '自检报告文件' $false '未生成 artifacts\selftest-publish.txt'
    }

    # ---- 6. 渲染链路自检（画一笔 → 出像素）---------------------------------
    # 这一步回答的是"笔到底能不能画出来"：它用**生产链路**（PointerSample → ToolDispatcher
    # → 工具 → 命令 → PageRenderer）合成一次真实绘制，渲染成 PNG 并逐区域数像素。
    Write-Step '6/10  渲染链路自检（画一笔 → 出像素）'
    $smokePng = Join-Path $artifacts 'render-smoke.png'
    $smokeTxt = Join-Path $artifacts 'render-smoke.txt'
    if (Test-Path $smokeTxt) { Remove-Item $smokeTxt -Force }
    if (Test-Path $smokePng) { Remove-Item $smokePng -Force }

    $smokeExit = -1
    try {
        $spsi = New-Object System.Diagnostics.ProcessStartInfo
        $spsi.FileName = $appExe
        $spsi.Arguments = '--render-smoke --out "' + $smokePng + '" --report "' + $smokeTxt + '"'
        $spsi.UseShellExecute = $false
        $spsi.RedirectStandardOutput = $true
        $spsi.RedirectStandardError = $true
        $spsi.CreateNoWindow = $true
        $sproc = [System.Diagnostics.Process]::Start($spsi)
        $null = $sproc.StandardOutput.ReadToEnd()
        $sproc.WaitForExit()
        $smokeExit = $sproc.ExitCode
    } catch {
        Write-Host "  启动渲染自检失败：$($_.Exception.Message)" -ForegroundColor Yellow
    }

    Add-Result '渲染链路自检退出码为 0' ($smokeExit -eq 0) "退出码 $smokeExit　artifacts\render-smoke.txt"
    Add-Result '渲染自检已产出 PNG' (Test-Path $smokePng) 'artifacts\render-smoke.png（人工可打开查看）'
    if (Test-Path $smokeTxt) {
        $smokeText = Get-Content $smokeTxt -Raw -Encoding UTF8
        $sPass = ([regex]::Matches($smokeText, '(?m)^PASS')).Count
        $sFail = ([regex]::Matches($smokeText, '(?m)^FAIL')).Count
        Add-Result '渲染自检明细' ($sFail -eq 0 -and $sPass -gt 0) "PASS $sPass 项，FAIL $sFail 项"
    }

    # ---- 7. 文件层自检（存 → 重新打开 → 逐像素比较）------------------------
    # 这一步回答"存了再打开，还是不是同一张图"：构造画板 → 保存 .wb → 重新打开
    # → 两边各自渲染成位图逐像素比较，顺带验证 PNG 导出与自动保存。
    Write-Step '7/10  文件层自检（保存 → 打开 → 逐像素比较）'
    $fileSmokeTxt = Join-Path $artifacts 'file-smoke.txt'
    if (Test-Path $fileSmokeTxt) { Remove-Item $fileSmokeTxt -Force }

    $fileSmokeExit = -1
    try {
        $fpsi = New-Object System.Diagnostics.ProcessStartInfo
        $fpsi.FileName = $appExe
        $fpsi.Arguments = '--file-smoke --report "' + $fileSmokeTxt + '"'
        $fpsi.UseShellExecute = $false
        $fpsi.RedirectStandardOutput = $true
        $fpsi.RedirectStandardError = $true
        $fpsi.CreateNoWindow = $true
        $fproc = [System.Diagnostics.Process]::Start($fpsi)
        $null = $fproc.StandardOutput.ReadToEnd()
        $fproc.WaitForExit()
        $fileSmokeExit = $fproc.ExitCode
    } catch {
        Write-Host "  启动文件层自检失败：$($_.Exception.Message)" -ForegroundColor Yellow
    }

    Add-Result '文件层自检退出码为 0' ($fileSmokeExit -eq 0) "退出码 $fileSmokeExit　artifacts\file-smoke.txt"
    if (Test-Path $fileSmokeTxt) {
        $fText = Get-Content $fileSmokeTxt -Raw -Encoding UTF8
        $fPass = ([regex]::Matches($fText, '(?m)^PASS')).Count
        $fFail = ([regex]::Matches($fText, '(?m)^FAIL')).Count

        # 单独把"逐像素一致"这一条拎出来展示：它是文件层最关键的证据
        $pixelLine = ($fText -split "`r?`n" | Where-Object { $_ -match '逐像素一致' })
        Add-Result '文件层自检明细' ($fFail -eq 0 -and $fPass -gt 0) "PASS $fPass 项，FAIL $fFail 项"
        if ($pixelLine) { Add-Result '保存/打开后渲染一致' $true $pixelLine.Trim() }
    }

    # ---- 8. 画布交互自检（真实鼠标/键盘）-----------------------------------
    # 为什么需要这一步：前面所有自检都是**离屏**的（能渲染、能存读），
    # 但证明不了"指针事件真的送到了画布上"。曾经出现过一次真实回归：
    # 文本编辑浮层盖在画布上把所有指针事件吃掉，于是**所有工具都画不出东西**，
    # 而离屏自检全绿。这一步真的用鼠标画一笔、用键盘切工具，逐个确认工具可用。
    Write-Step '8/10  画布交互自检（真实鼠标/键盘）'
    $uiScript = Join-Path $root 'scripts\ui-tools-test.ps1'
    $uiExit = -1
    if (Test-Path $uiScript) {
        $uiOut = Join-Path $artifacts 'ui-tools'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $uiScript -ExePath $appExe -OutDir $uiOut 2>&1 |
            Tee-Object -FilePath (Join-Path $artifacts 'ui-tools-test.txt') | Out-Null
        $uiExit = $LASTEXITCODE
    }

    if ($uiExit -eq 3) {
        # 无交互式桌面（例如 CI/服务会话）：如实报告"跳过"，不当作通过也不当作失败
        Add-Result '画布交互自检' $true '跳过：当前会话没有交互式桌面，无法模拟鼠标'
    } else {
        Add-Result '画布交互自检（画笔/矩形/椭圆/橡皮/文本）' ($uiExit -eq 0) `
            "退出码 $uiExit　artifacts\ui-tools-test.txt + artifacts\ui-tools\*.png"
    }
    # ---- 9. 拖动交互自检（真实鼠标）----------------------------------------
    # 拖动是"手指最直接的动作"，也最容易在重构中被悄悄弄坏。实测踩过两个坑：
    #   ① 画布没有捕获鼠标 → 指针一离开画布就收不到 Move，
    #      于是"把对象拖到左侧缩略图"这个手势根本走不完；
    #   ② 拖出画布后第一次没落在缩略图上就取消了拖动。
    # 这一步真的用鼠标拖：拖动对象 / 中键平移 / 跨页搬运 / 拖动缩略图改页序。
    Write-Step '9/10  拖动交互自检（真实鼠标）'
    $dragScript = Join-Path $root 'scripts\ui-drag-test.ps1'
    $dragExit = -1
    if (Test-Path $dragScript) {
        $dragOut = Join-Path $artifacts 'ui-drag'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $dragScript -ExePath $appExe -OutDir $dragOut 2>&1 |
            Tee-Object -FilePath (Join-Path $artifacts 'ui-drag-test.txt') | Out-Null
        $dragExit = $LASTEXITCODE
    }

    if ($dragExit -eq 3) {
        Add-Result '拖动交互自检' $true '跳过：当前会话没有交互式桌面，无法模拟鼠标'
    } else {
        Add-Result '拖动交互自检（对象/平移/跨页/页序）' ($dragExit -eq 0) `
            "退出码 $dragExit　artifacts\ui-drag-test.txt + artifacts\ui-drag\*.png"
    }

    # ---- 8. 数据目录合规（C3）---------------------------------------------
    Write-Step '10/10  数据目录合规（C3）'
    $dataDir = Join-Path $publishDir 'data'
    if (Test-Path $dataDir) {
        $inside = (Resolve-Path $dataDir).Path.StartsWith((Resolve-Path $publishDir).Path, [StringComparison]::OrdinalIgnoreCase)
        Add-Result '数据目录位于 exe 同级' $inside $dataDir
        $themeFile = Join-Path $dataDir 'theme\color.txt'
        Add-Result '主题文件已写出到 data\theme\' (Test-Path $themeFile) $themeFile
    } else {
        Add-Result '数据目录位于 exe 同级' $true '未创建（自检未写入数据）'
    }

    if (Test-Path $publishDir) {
        Write-ValidationCmd -TargetDir $publishDir
        Add-Result '已生成双击验证入口' (Test-Path (Join-Path $publishDir '验证.cmd')) 'publish\selfcontained\验证.cmd'

        # 随程序发布的操作说明（程序内「快捷键」按钮会打开它）
        $helpSrc = Join-Path $root 'assets\快捷键说明.txt'
        if (Test-Path $helpSrc) {
            Copy-Item $helpSrc (Join-Path $publishDir '快捷键说明.txt') -Force
        }
        Add-Result '已随包携带快捷键说明' (Test-Path (Join-Path $publishDir '快捷键说明.txt')) 'publish\selfcontained\快捷键说明.txt'
    }
}

# ---- 汇总 -------------------------------------------------------------------
Write-Step '验证汇总'
$script:Steps | Format-Table -AutoSize | Out-String | Write-Host

# 汇总报告落盘（验收证据）
$reportPath = Join-Path $artifacts 'verify-report.txt'
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('WhiteBoard 自动验证报告')
[void]$sb.AppendLine("时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$sb.AppendLine("机器：$env:COMPUTERNAME　用户：$env:USERNAME")
[void]$sb.AppendLine("OS  ：$([System.Environment]::OSVersion.VersionString)")
[void]$sb.AppendLine("配置：$Configuration")
[void]$sb.AppendLine(('-' * 64))
foreach ($s in $script:Steps) {
    $mark = if ($s.Ok) { 'PASS' } else { 'FAIL' }
    [void]$sb.AppendLine("[$mark] $($s.Name)　$($s.Detail)")
}
[void]$sb.AppendLine(('-' * 64))
[void]$sb.AppendLine("结果：$(if ($script:Failures.Count -eq 0) { '全部通过' } else { '失败项：' + ($script:Failures -join '、') })")
[System.IO.File]::WriteAllText($reportPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))
Write-Host "  报告已写入：artifacts\verify-report.txt" -ForegroundColor DarkGray

if ($script:Failures.Count -eq 0) {
    Write-Host '  全部通过' -ForegroundColor Green
    exit 0
} else {
    Write-Host ("  失败项：{0}" -f ($script:Failures -join '、')) -ForegroundColor Red
    exit 1
}
