# 拖动自检：把"拖动"这一类交互逐个用真实鼠标试一遍。
#
# 覆盖 4 条拖动路径（都是用户会直接感受到的）：
#   ① 选择工具拖动对象移动
#   ② 中键拖动平移画布
#   ③ 把选中的对象拖到左侧页面缩略图上（跨页搬运）
#   ④ 拖动页面缩略图调整页序
#
# 判定手段：**按颜色跟踪具体对象**，而不是统计全部非背景像素。
# 原因：选中时画布上会画一层半透明高亮（设计如此，投影时更醒目），
# 那层高亮会被"非背景像素"统计进去，看起来就像"拖动时把对象复制了一份"。
# 按颜色（示例板里矩形是黄色 #EED858）取包围盒就精确得多。
#
# 用法： powershell -ExecutionPolicy Bypass -File scripts\ui-drag-test.ps1 -ExePath <exe> [-OutDir <目录>]
# 退出码：0 = 全部通过；1 = 有拖动不工作；3 = 拿不到窗口

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$OutDir = '',
    [int]$WaitSeconds = 8
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WbDrag
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public const uint LEFTDOWN   = 0x0002;
    public const uint LEFTUP     = 0x0004;
    public const uint MIDDLEDOWN = 0x0020;
    public const uint MIDDLEUP   = 0x0040;
    public const uint KEYUP      = 0x0002;
    public const byte VK_CTRL    = 0x11;
    public const byte VK_ESC     = 0x1B;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

[void][WbDrag]::SetProcessDPIAware()

$exe = (Resolve-Path $ExePath).Path
$outFull = ''
if ($OutDir -ne '') {
    if ([System.IO.Path]::IsPathRooted($OutDir)) { $outFull = [System.IO.Path]::GetFullPath($OutDir) }
    else { $outFull = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutDir)) }
    if (-not (Test-Path $outFull)) { New-Item -ItemType Directory -Path $outFull -Force | Out-Null }
}

function Capture([IntPtr]$h, [int]$w, [int]$ht) {
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $gfx.GetHdc()
    [void][WbDrag]::PrintWindow($h, $hdc, 0x2)
    $gfx.ReleaseHdc($hdc)
    $gfx.Dispose()
    return $bmp
}

function Near([int]$v, [int]$target, [int]$tol = 18) { return [Math]::Abs($v - $target) -le $tol }

# 找出某个颜色在指定区域里的（像素数, 包围盒中心与范围）
function ColorBox($bmp, [int]$r, [int]$g, [int]$b, [int]$x0, [int]$y0, [int]$x1, [int]$y1) {
    $n = 0; $minX = 99999; $maxX = -1; $minY = 99999; $maxY = -1
    for ($y = $y0; $y -lt $y1; $y++) {
        for ($x = $x0; $x -lt $x1; $x++) {
            $c = $bmp.GetPixel($x, $y)
            if (-not (Near $c.R $r)) { continue }
            if (-not (Near $c.G $g)) { continue }
            if (-not (Near $c.B $b)) { continue }
            $n++
            if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
        }
    }
    if ($n -eq 0) { return [pscustomobject]@{ Count = 0; Cx = 0.0; Cy = 0.0; X0 = 0; Y0 = 0; X1 = 0; Y1 = 0 } }
    return [pscustomobject]@{ Count = $n; Cx = ($minX + $maxX) / 2.0; Cy = ($minY + $maxY) / 2.0; X0 = $minX; Y0 = $minY; X1 = $maxX; Y1 = $maxY }
}

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
    [WbDrag]::keybd_event($vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [WbDrag]::keybd_event($vk, 0, [WbDrag]::KEYUP, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}

function DragWith([uint32]$downFlag, [uint32]$upFlag, [int]$x, [int]$y, [int]$dx, [int]$dy, [int]$steps = 18, [int]$holdMs = 28) {
    [void][WbDrag]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 160
    [WbDrag]::mouse_event($downFlag, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    for ($i = 1; $i -le $steps; $i++) {
        [void][WbDrag]::SetCursorPos($x + [int]($dx * $i / $steps), $y + [int]($dy * $i / $steps))
        Start-Sleep -Milliseconds $holdMs
    }
    [WbDrag]::mouse_event($upFlag, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 450
}

function ClearSelection() {
    [WbDrag]::keybd_event([WbDrag]::VK_ESC, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [WbDrag]::keybd_event([WbDrag]::VK_ESC, 0, [WbDrag]::KEYUP, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}

# ── 启动（--demo：自带 2 页示例板，正好用来测跨页与页序） ──────────────────

$proc = Start-Process -FilePath $exe -ArgumentList '--demo' -PassThru
Write-Host "已启动 PID=$($proc.Id)（--demo，2 页示例画板），等待窗口…"

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

$rect = [WbDrag+RECT]::new()
[void][WbDrag]::GetWindowRect($hwnd, [ref]$rect)
$winW = $rect.Right - $rect.Left
$winH = $rect.Bottom - $rect.Top
[void][WbDrag]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 500

$cx0 = 178; $cy0 = 115; $cx1 = $winW - 4; $cy1 = $winH - 70

$results = New-Object System.Collections.Generic.List[object]
function Report([string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    Write-Host ("  [{0}] {1}　{2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

Write-Host ''
Write-Host '── 拖动交互逐个检查（真实鼠标）──'

# 示例板里矩形是 #EED858，但同一个黄色还用在"标题文字"和"第 2 条波浪笔迹"上，
# 所以探测必须限定在**只有矩形**的那块区域（左上角避开文字、上部避开波浪）。
$YR = 0xEE; $YG = 0xD8; $YB = 0x58
$probeX0 = $cx0; $probeY0 = 430; $probeX1 = [Math]::Min($cx1, 780); $probeY1 = $cy1
Write-Host ("     探测区域（只含矩形的区域）：x {0}..{1}　y {2}..{3}" -f $probeX0, $probeX1, $probeY0, $probeY1)

# ── ① 选择工具拖动对象（抓住矩形上边框拖动）────────────────────────────────

$shot = Wait-UntilPainted $hwnd $winW $winH
if ($null -eq $shot) {
    Write-Warning '窗口在 15 秒内没有画出来（可能没有交互式桌面）'
    $proc.Kill()
    exit 3
}
$rect0 = ColorBox $shot $YR $YG $YB $probeX0 $probeY0 $probeX1 $probeY1
if ($outFull -ne '') { $shot.Save((Join-Path $outFull '0-初始.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
$shot.Dispose()

if ($rect0.Count -lt 100) {
    Report '找到示例板里的矩形（黄色）' $false "黄色像素只有 $($rect0.Count)，用例前提不成立"
} else {
    Write-Host ("     矩形初始包围盒 x {0}..{1}　y {2}..{3}（像素 {4}）" -f $rect0.X0, $rect0.X1, $rect0.Y0, $rect0.Y1, $rect0.Count)

    PressKey 0x36   # '6' 选择

    # 抓住矩形**上边框**（中心 x、上沿 +2），这样是"点中对象再拖动"，不是框选
    $grabX = $rect.Left + [int]$rect0.Cx
    $grabY = $rect.Top + [int]$rect0.Y0 + 2
    $dx = 80; $dy = 50
    DragWith ([WbDrag]::LEFTDOWN) ([WbDrag]::LEFTUP) $grabX $grabY $dx $dy 18

    ClearSelection   # 取消选中，避免半透明高亮层干扰像素统计
    $shot = Capture $hwnd $winW $winH
    if ($outFull -ne '') { $shot.Save((Join-Path $outFull '1-拖动对象.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $rect1 = ColorBox $shot $YR $YG $YB $probeX0 $probeY0 $probeX1 $probeY1
    $shot.Dispose()

    $movedX = $rect1.Cx - $rect0.Cx
    $movedY = $rect1.Cy - $rect0.Cy
    $keptRatio = if ($rect0.Count -eq 0) { 0 } else { $rect1.Count / $rect0.Count }

    Report '选择工具拖动对象（矩形跟着动）' `
        ([Math]::Abs($movedX) -gt $dx * 0.6 -and [Math]::Abs($movedY) -gt $dy * 0.6) `
        ("矩形中心位移 dx={0:0.0} dy={1:0.0}（拖了 {2},{3}）" -f $movedX, $movedY, $dx, $dy)

    Report '拖动是移动而不是复制（黄色像素数不变）' ($keptRatio -gt 0.85 -and $keptRatio -lt 1.15) `
        ("黄色像素 {0} → {1}（保持率 {2:P0}）" -f $rect0.Count, $rect1.Count, $keptRatio)
}

# ── ② 中键拖动平移画布 ────────────────────────────────────────────────────

$shot = Capture $hwnd $winW $winH
$b0 = ColorBox $shot $YR $YG $YB $probeX0 $probeY0 $probeX1 $probeY1
$shot.Dispose()

$pdx = 40; $pdy = -20
DragWith ([WbDrag]::MIDDLEDOWN) ([WbDrag]::MIDDLEUP) ($rect.Left + 950) ($rect.Top + 260) $pdx $pdy 16

$shot = Capture $hwnd $winW $winH
if ($outFull -ne '') { $shot.Save((Join-Path $outFull '2-平移.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
$b1 = ColorBox $shot $YR $YG $YB $probeX0 $probeY0 $probeX1 $probeY1
$shot.Dispose()

$panMovedX = $b1.Cx - $b0.Cx
$panKept = if ($b0.Count -eq 0) { 0 } else { $b1.Count / $b0.Count }

Report '中键拖动平移画布' ([Math]::Abs($panMovedX) -gt [Math]::Abs($pdx) * 0.5) `
    ("矩形中心跟着平移 dx={0:0.0}（拖了 {1}）" -f $panMovedX, $pdx)
Report '平移只移动视图不改内容' ($panKept -gt 0.9 -and $panKept -lt 1.1) `
    ("黄色像素保持率 {0:P0}" -f $panKept)

# 复位视图，让后续步骤的坐标假设重新成立
[WbDrag]::keybd_event([WbDrag]::VK_CTRL, 0, 0, [UIntPtr]::Zero)
PressKey 0x30   # '0'
[WbDrag]::keybd_event([WbDrag]::VK_CTRL, 0, [WbDrag]::KEYUP, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 500

# ── ③ 跨页搬运：全选后拖到左侧第 2 页缩略图 ───────────────────────────────

$shot = Capture $hwnd $winW $winH
$c0 = ColorBox $shot $YR $YG $YB $probeX0 $probeY0 $probeX1 $probeY1
$shot.Dispose()

[WbDrag]::keybd_event([WbDrag]::VK_CTRL, 0, 0, [UIntPtr]::Zero)
PressKey 0x41   # 'A' 全选
[WbDrag]::keybd_event([WbDrag]::VK_CTRL, 0, [WbDrag]::KEYUP, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 400

$grabX = $rect.Left + [int]$c0.Cx
$grabY = $rect.Top + [int]$c0.Y0 + 2
$thumb2X = $rect.Left + 90
$thumb2Y = $rect.Top + 470
DragWith ([WbDrag]::LEFTDOWN) ([WbDrag]::LEFTUP) $grabX $grabY ($thumb2X - $grabX) ($thumb2Y - $grabY) 24 30

$shot = Capture $hwnd $winW $winH
if ($outFull -ne '') { $shot.Save((Join-Path $outFull '3-跨页搬运.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
$c1 = ColorBox $shot $YR $YG $YB $probeX0 $probeY0 $probeX1 $probeY1
$shot.Dispose()

Report '拖动到缩略图实现跨页搬运' ($c1.Count -lt $c0.Count * 0.3) `
    ("画布上的黄色像素 {0} → {1}（搬到第 2 页后当前页应几乎没有它）" -f $c0.Count, $c1.Count)

# ── ④ 拖动缩略图调整页序 ──────────────────────────────────────────────────

$before = Capture $hwnd $winW $winH
if ($outFull -ne '') { $before.Save((Join-Path $outFull '4a-页序前.png'), [System.Drawing.Imaging.ImageFormat]::Png) }

# 拖动第 1 页缩略图到第 2 页位置（两页内容不同，页序交换后缩略图区应有明显变化）
DragWith ([WbDrag]::LEFTDOWN) ([WbDrag]::LEFTUP) ($rect.Left + 90) ($rect.Top + 320) 0 150 16

$after = Capture $hwnd $winW $winH
if ($outFull -ne '') { $after.Save((Join-Path $outFull '4b-页序后.png'), [System.Drawing.Imaging.ImageFormat]::Png) }

$reorderDiff = DiffCount $before $after 4 $cy0 174 $cy1
$before.Dispose(); $after.Dispose()

Report '拖动缩略图调整页序' ($reorderDiff -gt 300) `
    ("页面目录区域变化像素 $reorderDiff（页序交换后两张缩略图内容应互换）")

# ── 汇总 ──────────────────────────────────────────────────────────────────

if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 600 }
if (-not $proc.HasExited) { $proc.Kill() }

Write-Host ''
$failed = @($results | Where-Object { -not $_.Ok })
Write-Host ("结果：通过 {0}　失败 {1}　共 {2} 项" -f ($results.Count - $failed.Count), $failed.Count, $results.Count)
if ($outFull -ne '') { Write-Host "截图目录：$outFull" }

if ($failed.Count -gt 0) {
    Write-Warning ('不工作的拖动：' + (($failed | ForEach-Object { $_.Name }) -join '、'))
    exit 1
}
Write-Host '全部通过：拖动对象 / 中键平移 / 跨页搬运 / 页序调整 都正常工作'
exit 0
