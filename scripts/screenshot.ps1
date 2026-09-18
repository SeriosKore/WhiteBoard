# 截取正在运行的白板窗口（用于人工验收留证）
# 用法： powershell -ExecutionPolicy Bypass -File scripts\screenshot.ps1 -ExePath <exe> -OutPath <png> [-Demo] [-Size 1000x700] [-WaitSeconds 6]
#
# 说明：PrintWindow + PW_RENDERFULLCONTENT(0x2) 可以在窗口不置前的情况下抓到 WPF 的内容。
# 若目标机器没有交互式桌面（例如纯服务器/无会话），抓到的图会是纯黑——脚本会以退出码 2 明确报告，
# 而不是产出一张看似正常实则无效的图。

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutPath,
    [switch]$Demo,
    [string]$Size = '',
    [string[]]$AppArgs = @(),
    [int]$WaitSeconds = 6
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WinCap
{
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$exe = (Resolve-Path $ExePath).Path
if ([System.IO.Path]::IsPathRooted($OutPath)) { $outFull = [System.IO.Path]::GetFullPath($OutPath) } else { $outFull = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutPath)) }
$outDir = [System.IO.Path]::GetDirectoryName($outFull)
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

# 组装启动参数。
# 注意：PowerShell 变量名不区分大小写，所以这里**不能**用 $appArgs 去拼接 $AppArgs
# （那是同一个变量，会把自己加到自己身上）；另外 Start-Process -ArgumentList 传数组时
# 对空元素很挑剔，统一拼成一个字符串最稳。
$launchArgs = New-Object System.Collections.Generic.List[string]
if ($Demo) { $launchArgs.Add('--demo') }
if ($Size -ne '') { $launchArgs.Add('--size'); $launchArgs.Add($Size) }
foreach ($a in $AppArgs) { if ($a) { $launchArgs.Add($a) } }
$argLine = ($launchArgs -join ' ')

$proc = Start-Process -FilePath $exe -ArgumentList $argLine -PassThru
Write-Host "已启动 PID=$($proc.Id)　参数：$argLine　等待窗口…"

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
    Write-Error "未能在 $WaitSeconds 秒内拿到主窗口句柄（应用可能启动失败）"
    exit 3
}

# 让窗口稳定一帧（WPF 首次布局 + 渲染）
Start-Sleep -Milliseconds 900
[void][WinCap]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 400

$rect = [WinCap+RECT]::new()
[void][WinCap]::GetWindowRect($hwnd, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
Write-Host "窗口尺寸：${w}x${h}"

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $gfx.GetHdc()
$ok = [WinCap]::PrintWindow($hwnd, $hdc, 0x2)
$gfx.ReleaseHdc($hdc)
$gfx.Dispose()

$bmp.Save($outFull, [System.Drawing.Imaging.ImageFormat]::Png)

# 判断是否抓到有效内容：统计非黑像素比例
$sampled = 0
$nonBlack = 0
for ($y = 0; $y -lt $h; $y += 7) {
    for ($x = 0; $x -lt $w; $x += 7) {
        $c = $bmp.GetPixel($x, $y)
        $sampled++
        if ($c.R -gt 8 -or $c.G -gt 8 -or $c.B -gt 8) { $nonBlack++ }
    }
}
$bmp.Dispose()

$ratio = if ($sampled -gt 0) { [math]::Round(100.0 * $nonBlack / $sampled, 1) } else { 0 }
Write-Host "PrintWindow=$ok　非黑像素比例=${ratio}%　→ $outFull"

if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 600 }
if (-not $proc.HasExited) { $proc.Kill() }

if ($ratio -lt 5) {
    Write-Warning "抓到的图基本是黑的（比例 ${ratio}%）：这台机器可能没有交互式桌面会话，截图不可用。"
    exit 2
}

exit 0
