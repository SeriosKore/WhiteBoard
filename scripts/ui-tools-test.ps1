# 画布交互自检：真的用鼠标/键盘操作一遍所有工具，逐个检查画布上的像素变化。
#
# 为什么必须单独做这一套：render-smoke / file-smoke 都是**离屏**验证（能渲染、能存读），
# 但证明不了"指针事件真的送到了画布上"。曾经出现过"文字编辑浮层把画布盖住、
# 于是所有工具都画不出东西"这种回归——只有走真实输入链路才能发现。
#
# 用法： powershell -ExecutionPolicy Bypass -File scripts\ui-tools-test.ps1 -ExePath <exe> [-OutDir <目录>]
# 退出码：0 = 全部通过；1 = 有工具不工作；3 = 拿不到窗口（无交互式桌面）

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$OutDir = '',
    [int]$WaitSeconds = 8
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WbUi
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP   = 0x0004;
    public const uint KEYUP    = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

[void][WbUi]::SetProcessDPIAware()

$exe = (Resolve-Path $ExePath).Path
$outFull = ''
if ($OutDir -ne '') {
    if ([System.IO.Path]::IsPathRooted($OutDir)) { $outFull = [System.IO.Path]::GetFullPath($OutDir) } else { $outFull = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutDir)) }
    if (-not (Test-Path $outFull)) { New-Item -ItemType Directory -Path $outFull -Force | Out-Null }
}

# ── 辅助函数 ──────────────────────────────────────────────────────────────

function Capture([IntPtr]$h, [int]$w, [int]$ht) {
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $gfx.GetHdc()
    [void][WbUi]::PrintWindow($h, $hdc, 0x2)
    $gfx.ReleaseHdc($hdc)
    $gfx.Dispose()
    return $bmp
}

# 统计两块位图在指定矩形内变化的像素数
function DiffCount($a, $b, [int]$x0, [int]$y0, [int]$x1, [int]$y1) {
    $n = 0
    for ($y = $y0; $y -lt $y1; $y++) {
        for ($x = $x0; $x -lt $x1; $x++) {
            $p = $a.GetPixel($x, $y); $q = $b.GetPixel($x, $y)
            if ([Math]::Abs($p.R - $q.R) -gt 6 -or [Math]::Abs($p.G - $q.G) -gt 6 -or [Math]::Abs($p.B - $q.B) -gt 6) { $n++ }
        }
    }
    return $n
}

# 统计某区域里"不是背景色"的像素数（背景 = 黑板绿 #2F4F3A）
function NonBackground($bmp, [int]$x0, [int]$y0, [int]$x1, [int]$y1) {
    $n = 0
    for ($y = $y0; $y -lt $y1; $y++) {
        for ($x = $x0; $x -lt $x1; $x++) {
            $c = $bmp.GetPixel($x, $y)
            if (-not ([Math]::Abs($c.R - 0x2F) -le 12 -and [Math]::Abs($c.G - 0x4F) -le 12 -and [Math]::Abs($c.B - 0x3A) -le 12)) { $n++ }
        }
    }
    return $n
}

# 等窗口真的画出来为止（轮询画布背景色，而不是死等固定毫秒数）。
# 实测：机器忙的时候 WPF 首帧可能比固定 sleep 更晚，PrintWindow 会抓到一张纯白窗口，
# 于是所有像素统计全部失真、用例结论完全是假的。
function Wait-UntilPainted([IntPtr]$h, [int]$w, [int]$ht, [int]$timeoutSec = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $bmp = Capture $h $w $ht
        $n = 0
        for ($y = 115; $y -lt ($ht - 70); $y += 6) {
            for ($x = 178; $x -lt ($w - 4); $x += 6) {
                $c = $bmp.GetPixel($x, $y)
                if ([Math]::Abs($c.R - 0x2F) -le 10 -and [Math]::Abs($c.G - 0x4F) -le 10 -and [Math]::Abs($c.B - 0x3A) -le 10) { $n++ }
            }
        }
        if ($n -gt 200) { return $bmp }   # 画布已经画好，这一帧直接当初始帧用
        $bmp.Dispose()
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function PressKey([byte]$vk) {
    [WbUi]::keybd_event($vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [WbUi]::keybd_event($vk, 0, [WbUi]::KEYUP, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 220
}

# 一次完整的鼠标拖拽（屏幕绝对坐标）
function DragPath([int]$x, [int]$y, [int]$dx, [int]$dy, [int]$steps = 20) {
    [void][WbUi]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 120
    [WbUi]::mouse_event([WbUi]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    for ($i = 1; $i -le $steps; $i++) {
        [void][WbUi]::SetCursorPos($x + [int]($dx * $i / $steps), $y + [int]($dy * $i / $steps))
        Start-Sleep -Milliseconds 25
    }
    [WbUi]::mouse_event([WbUi]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}

function Click([int]$x, [int]$y) {
    [void][WbUi]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 120
    [WbUi]::mouse_event([WbUi]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [WbUi]::mouse_event([WbUi]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

# ── 启动 ──────────────────────────────────────────────────────────────────

$proc = Start-Process -FilePath $exe -PassThru
Write-Host "已启动 PID=$($proc.Id)（空白画板），等待窗口…"

$hwnd = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    $proc.Refresh()
    if ($proc.HasExited) { break }
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle; break }
}

if ($hwnd -eq [IntPtr]::Zero) {
    if (-not $proc.HasExited) { $proc.Kill() }
    Write-Warning '未能拿到主窗口句柄（可能没有交互式桌面）'
    exit 3
}

$rect = [WbUi+RECT]::new()
[void][WbUi]::GetWindowRect($hwnd, [ref]$rect)
$winW = $rect.Right - $rect.Left
$winH = $rect.Bottom - $rect.Top
[void][WbUi]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 500

# 画布区域（避开左侧页面目录 176px、上方两条工具栏 ~115px、下方状态栏 ~70px）
$cx0 = 178; $cy0 = 115; $cx1 = $winW - 4; $cy1 = $winH - 70

# 各工具用互相不重叠的子区域，便于分别判定
$penX = $rect.Left + 240; $penY = $rect.Top + 200
$rectX = $rect.Left + 240; $rectY = $rect.Top + 420
$textX = $rect.Left + 700; $textY = $rect.Top + 200

$results = New-Object System.Collections.Generic.List[object]
function Report([string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1}　{2}" -f $mark, $name, $detail)
}

Write-Host ''
Write-Host '── 逐个工具检查（真实鼠标/键盘输入）──'

# ── ① 画笔（快捷键 1）────────────────────────────────────────────────────

$before = Wait-UntilPainted $hwnd $winW $winH
if ($null -eq $before) { Write-Warning '窗口在 15 秒内没有画出来'; $proc.Kill(); exit 3 }
PressKey 0x31   # '1'
DragPath $penX $penY 360 0 24
$after = Capture $hwnd $winW $winH
$penDiff = DiffCount $before $after $cx0 $cy0 $cx1 $cy1
Report '画笔（鼠标拖拽）' ($penDiff -gt 300) "画布变化像素 $penDiff"

if ($outFull -ne '') { $after.Save((Join-Path $outFull '1-画笔.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
$before.Dispose()

# ── ② 矩形（快捷键 3）────────────────────────────────────────────────────

$before = Capture $hwnd $winW $winH
PressKey 0x33   # '3'
DragPath $rectX $rectY 300 150 20
$after = Capture $hwnd $winW $winH
$rectDiff = DiffCount $before $after $cx0 $cy0 $cx1 $cy1

# 空心矩形：四条边框上都有像素、正中心仍然是背景色。
# 注意边带要留 ±8px：笔宽 6 世界单位 + 上下 Border 的 1px，描边中心并不精确落在按下点上。
$rL = $rectX - $rect.Left; $rT = $rectY - $rect.Top
$topEdge    = NonBackground $after ($rL + 20) ($rT - 8) ($rL + 280) ($rT + 8)
$bottomEdge = NonBackground $after ($rL + 20) ($rT + 142) ($rL + 280) ($rT + 158)
$leftEdge   = NonBackground $after ($rL - 8) ($rT + 20) ($rL + 8) ($rT + 130)
$rightEdge  = NonBackground $after ($rL + 292) ($rT + 20) ($rL + 308) ($rT + 130)
$centerPixels = NonBackground $after ($rL + 140) ($rT + 65) ($rL + 160) ($rT + 85)

Report '矩形（拖拽绘制）' ($rectDiff -gt 300) "画布变化像素 $rectDiff"

Report '矩形是空心描边' (($topEdge + $bottomEdge + $leftEdge + $rightEdge) -gt 300 -and $centerPixels -eq 0) "四边像素 $topEdge/$bottomEdge/$leftEdge/$rightEdge，中心像素 $centerPixels"

if ($outFull -ne '') { $after.Save((Join-Path $outFull '2-矩形.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
$before.Dispose()

# ── ③ 椭圆（快捷键 4）────────────────────────────────────────────────────

$before = Capture $hwnd $winW $winH
PressKey 0x34   # '4'
DragPath ($rectX + 360) $rectY 260 150 20
$after = Capture $hwnd $winW $winH
$ellipseDiff = DiffCount $before $after $cx0 $cy0 $cx1 $cy1
Report '椭圆（拖拽绘制）' ($ellipseDiff -gt 300) "画布变化像素 $ellipseDiff"

# ── ④ 橡皮（快捷键 5）：擦掉画笔那条线的中段 ──────────────────────────────

$before = Capture $hwnd $winW $winH
PressKey 0x35   # '5'
DragPath ($penX + 180) ($penY - 40) 0 80 10
$after = Capture $hwnd $winW $winH
$eraseDiff = DiffCount $before $after $cx0 $cy0 $cx1 $cy1

# 被擦的位置应当回到背景色
$gapPixels = NonBackground $after ($penX - $rect.Left + 170) ($penY - $rect.Top - 8) ($penX - $rect.Left + 190) ($penY - $rect.Top + 8)
Report '橡皮（擦出缺口）' ($eraseDiff -gt 100) "画布变化像素 $eraseDiff"
Report '擦除处回到背景色' ($gapPixels -eq 0) "缺口处残留像素 $gapPixels"

if ($outFull -ne '') { $after.Save((Join-Path $outFull '3-橡皮.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
$before.Dispose()

# ── ⑤ 文本（快捷键 7）：点一下 → 打字 → Ctrl+Enter ────────────────────────

$before = Capture $hwnd $winW $winH
PressKey 0x37   # '7'
Click $textX $textY
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait('Test 123')
Start-Sleep -Milliseconds 400
# Ctrl+Enter 提交
[WbUi]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)          # Ctrl down
Start-Sleep -Milliseconds 60
PressKey 0x0D                                              # Enter
[WbUi]::keybd_event(0x11, 0, [WbUi]::KEYUP, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 600

$after = Capture $hwnd $winW $winH
$textDiff = DiffCount $before $after ($textX - $rect.Left - 10) ($textY - $rect.Top - 10) ($textX - $rect.Left + 400) ($textY - $rect.Top + 120)
Report '文本（就地输入并提交）' ($textDiff -gt 100) "文字区域变化像素 $textDiff"

if ($outFull -ne '') { $after.Save((Join-Path $outFull '4-文本.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
$before.Dispose()
$after.Dispose()

# ── ⑥ 选择 + 撤销：确认键盘与命令链也通 ──────────────────────────────────

PressKey 0x36   # '6' 选择
$before = Capture $hwnd $winW $winH
PressKey 0x1B   # Esc（退出全屏/取消选择，不应改变画布）
$after = Capture $hwnd $winW $winH
$escBefore = NonBackground $before $cx0 $cy0 $cx1 $cy1
$escAfter  = NonBackground $after  $cx0 $cy0 $cx1 $cy1
$escOk = [Math]::Abs($escBefore - $escAfter) -lt 300
$escDelta = [Math]::Abs($escBefore - $escAfter)
Report 'Esc 不改变画布内容' $escOk "内容像素 $escBefore → $escAfter（差 $escDelta）"

if (-not $escOk -and $outFull -ne '') {
    $before.Save((Join-Path $outFull 'esc-before.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $after.Save((Join-Path $outFull 'esc-after.png'), [System.Drawing.Imaging.ImageFormat]::Png)
}
# Ctrl+Z 撤销刚才那次选择切换不应报错；这里只验证不崩
[WbUi]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
PressKey 0x5A   # Z
[WbUi]::keybd_event(0x11, 0, [WbUi]::KEYUP, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 500
$alive = -not $proc.HasExited
Report 'Ctrl+Z 后程序仍正常运行' $alive $(if ($alive) { '进程存活' } else { '进程已退出' })

$after.Dispose()

# ── 汇总 ──────────────────────────────────────────────────────────────────

if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 600 }
if (-not $proc.HasExited) { $proc.Kill() }

Write-Host ''
$failed = @($results | Where-Object { -not $_.Ok })
Write-Host ("结果：通过 {0}　失败 {1}　共 {2} 项" -f ($results.Count - $failed.Count), $failed.Count, $results.Count)
if ($outFull -ne '') { Write-Host "截图目录：$outFull" }

if ($failed.Count -gt 0) {
    Write-Warning ('不工作的工具：' + (($failed | ForEach-Object { $_.Name }) -join '、'))
    exit 1
}

Write-Host '全部通过：画笔/矩形/椭圆/橡皮/文本 在真实输入下都能正常工作'
exit 0
