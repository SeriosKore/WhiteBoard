# 项目备份：把"源码 + 文档 + 脚本 + 测试 + 证据"打包成一个带时间戳的副本。
#
# 设计取舍：
#   · **不含 bin / obj / publish** —— 这三样都能由 scripts\verify.ps1 一条命令重建
#     （含 140 MB 的自包含发布产物），放进来只会让备份从 2.7 MB 涨到 290 MB。
#   · **含 artifacts / 验收结果** —— 这是验证证据，不可重建，必须留。
#   · 同时产出一个 zip，便于拷到 U 盘/网盘。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\backup.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\backup.ps1 -OutRoot "D:\备份"
#   powershell -ExecutionPolicy Bypass -File scripts\backup.ps1 -IncludePublish    # 连同 140MB 发布产物一起
#
# 退出码：0 = 成功

param(
    [string]$OutRoot = '',
    [switch]$IncludePublish,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'

if ($OutRoot -eq '') {
    # 默认放在项目**外面**的兄弟目录，避免"备份套备份"
    $parent = Split-Path $root -Parent
    $OutRoot = Join-Path $parent '_备份'
}
$outDir = Join-Path $OutRoot ("WhiteBoard_" + $stamp)

Write-Host "项目目录：$root"
Write-Host "备份目标：$outDir"
Write-Host ''

# 需要排除的目录（可重建）
$excludeDirs = @('bin', 'obj', '.vs', 'TestResults')
if (-not $IncludePublish) { $excludeDirs += 'publish' }

$excludePattern = '\\(' + (($excludeDirs | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\\'

$allFiles = Get-ChildItem $root -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch $excludePattern }

Write-Host "待复制文件：$($allFiles.Count) 个"

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$copied = 0
foreach ($f in $allFiles) {
    $rel = $f.FullName.Substring($root.Length).TrimStart('\')
    $dest = Join-Path $outDir $rel
    $destDir = Split-Path $dest -Parent
    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
    Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
    $copied++
}

$sizeMB = [math]::Round(((Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 2)
Write-Host "已复制 $copied 个文件（$sizeMB MB）"

# ── 写备份说明（含恢复与验证步骤） ────────────────────────────────────────
$pubLine = if ($IncludePublish) { '**已包含** `publish\selfcontained`（约 140 MB 的自包含发布产物，可直接运行）' }
           else { '**未包含** `publish\`（自包含发布产物，约 140 MB）。需要时用 `scripts\verify.ps1` 重新生成' }

$readme = @"
# 白板项目备份说明

- 备份时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
- 源目录：``$root``
- 备份目录：``$outDir``
- 文件数：$copied　体积：$sizeMB MB

## 包含什么

| 内容 | 说明 |
|---|---|
| ``src\`` | 全部源码（Core / Rendering / App），**不含** bin、obj |
| ``tests\`` | 两套单元测试（Core 117 项 + Rendering 108 项） |
| ``tools\`` ``poc\`` | PE 依赖扫描工具、笔迹延迟 PoC、笔迹稳定性探针 |
| ``scripts\`` | verify / build / screenshot / ui-tools-test / ui-drag-test / backup |
| ``documents\`` | 开发文档、计划书、评审、进度、人工验收清单、用户手册 |
| ``assets\`` | 随包发布的《快捷键说明.txt》 |
| ``artifacts\`` | **验证证据**（verify 报告、渲染/文件/交互自检报告与截图） |
| ``验收结果\`` | 人工验收交接目录（窗口截图等） |
| ``旧文件\`` | 已废弃的旧项目归档（仅存档，零复用） |

$pubLine

## 怎么恢复

1. 把整个备份目录复制到任意位置（路径不要太深、不要有权限限制）。
2. 需要运行程序：

       powershell -ExecutionPolicy Bypass -File scripts\verify.ps1

   这一条命令会：构建 7 个工程 → 跑 225 项单元测试 → PoC 冒烟 →
   自包含发布（生成 ``publish\selfcontained``）→ PE 依赖审计 → 三套自检 →
   画布交互自检 → 拖动交互自检 → 数据目录合规。**全部通过时退出码为 0**。
3. 只想快速判断"这份备份能不能用"：看 ``artifacts\verify-report.txt`` 的最后一行
   （应为"结果：全部通过"），或直接重跑上面的命令。

## 环境要求

- Windows 10 / 11
- 构建需要 **.NET SDK 10**（仅开发/构建需要）
- **运行不需要任何环境**：``publish\selfcontained`` 自带运行时，复制即用、不写注册表

## 注意

- 备份是**静态快照**，不含版本历史（本项目不是 git 仓库）。
  后续继续开发时请重新执行 ``scripts\backup.ps1`` 生成新的一份。
- ``bin`` / ``obj`` 未备份，这是刻意的：它们是构建中间产物，恢复后第一次构建会重新生成。
"@

$readmePath = Join-Path $outDir '备份说明.md'
[System.IO.File]::WriteAllText($readmePath, $readme, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "已写入 备份说明.md"

# ── 打包 zip ──────────────────────────────────────────────────────────────
$zipPath = ''
if (-not $NoZip) {
    $zipPath = Join-Path $OutRoot ("WhiteBoard_" + $stamp + ".zip")
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host '正在压缩…'
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $zipMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host "已生成 zip：$zipPath（$zipMB MB）"
}

Write-Host ''
Write-Host '──────── 备份完成 ────────'
Write-Host "目录：$outDir"
if ($zipPath -ne '') { Write-Host "压缩包：$zipPath" }
Write-Host "文件数：$copied　目录体积：$sizeMB MB"
exit 0
