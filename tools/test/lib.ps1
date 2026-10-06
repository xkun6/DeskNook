# 桌面整理 自动化测试库（PowerShell 5.1）。用法：. $PSScriptRoot\lib.ps1
# 注意：测试文件只允许 xk-test- 前缀，绝不触碰用户已有桌面文件。
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ('DnTest.Native' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace DnTest {
  public static class Native {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION {
      [FieldOffset(0)] public MOUSEINPUT mi;
      [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }

    public static void Mouse(uint flags) {
      INPUT[] a = new INPUT[1];
      a[0].type = 0; a[0].u.mi.dwFlags = flags;
      SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
    }
    public static void Key(ushort vk, ushort scan, uint flags) {
      INPUT[] a = new INPUT[1];
      a[0].type = 1; a[0].u.ki.vk = vk; a[0].u.ki.scan = scan; a[0].u.ki.flags = flags;
      SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
    }
    public static string FgTitle() {
      StringBuilder sb = new StringBuilder(512);
      GetWindowText(GetForegroundWindow(), sb, 512);
      return sb.ToString();
    }
    // 兜底：把 Progman/WorkerW 下的 SysListView32 重新显示
    public static int ShowSystemIcons() {
      int n = 0;
      IntPtr pm = FindWindow("Progman", null);
      IntPtr dv = pm == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(pm, IntPtr.Zero, "SHELLDLL_DefView", null);
      if (dv != IntPtr.Zero) { IntPtr lv = FindWindowEx(dv, IntPtr.Zero, "SysListView32", null); if (lv != IntPtr.Zero) { ShowWindow(lv, 5); n++; } }
      int cnt = 0;
      EnumWindows(delegate(IntPtr h, IntPtr l) {
        StringBuilder sb = new StringBuilder(256); GetClassName(h, sb, 256);
        if (sb.ToString() == "WorkerW") {
          IntPtr d = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
          if (d != IntPtr.Zero) { IntPtr lv2 = FindWindowEx(d, IntPtr.Zero, "SysListView32", null); if (lv2 != IntPtr.Zero) { ShowWindow(lv2, 5); cnt++; } }
        }
        return true;
      }, IntPtr.Zero);
      return n + cnt;
    }
  }
}
'@
}

# 进程 DPI 感知：PerMonitorV2（-4），坐标一律为物理像素
[void][DnTest.Native]::SetProcessDpiAwarenessContext([IntPtr](-4))

# ---------- 路径 ----------
$Script:TestRoot = $PSScriptRoot
$Script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Script:OutDir   = Join-Path $PSScriptRoot 'out'
if (-not (Test-Path $Script:OutDir)) { New-Item -ItemType Directory -Path $Script:OutDir | Out-Null }
$Script:ExePath  = Join-Path $Script:RepoRoot 'src\DeskNook\bin\Release\net9.0-windows\DeskNook.exe'
$Script:DataDir  = Join-Path (Split-Path $Script:ExePath -Parent) 'data'   # 数据一律在程序目录下的 data
$Script:LogPath  = Join-Path $Script:DataDir 'logs\desknook.log'
$Script:DesktopDir = [Environment]::GetFolderPath('Desktop')
$Script:Results  = New-Object System.Collections.ArrayList

# 虚拟屏幕 @(x,y,w,h)
function Get-VirtualScreen {
    $x = [DnTest.Native]::GetSystemMetrics(76); $y = [DnTest.Native]::GetSystemMetrics(77)
    $w = [DnTest.Native]::GetSystemMetrics(78); $h = [DnTest.Native]::GetSystemMetrics(79)
    return @($x, $y, $w, $h)
}

# ---------- 截图与图像 ----------
function Save-Screen {
    param([Parameter(Mandatory)][string]$Name, [int[]]$Rect)
    if (-not $Rect) { $Rect = Get-VirtualScreen }
    $path = Join-Path $Script:OutDir ($Name + '.png')
    $bmp = New-Object System.Drawing.Bitmap $Rect[2], $Rect[3]
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($Rect[0], $Rect[1], 0, 0, $bmp.Size)
        $g.Dispose()
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bmp.Dispose() }
    return $path
}

function Crop-Image {
    param([Parameter(Mandatory)][string]$Src, [Parameter(Mandatory)][string]$Dst, [int]$X, [int]$Y, [int]$W, [int]$H)
    $img = [System.Drawing.Bitmap]::FromFile($Src)
    try {
        $rect = New-Object System.Drawing.Rectangle $X, $Y, $W, $H
        $crop = $img.Clone($rect, $img.PixelFormat)
        try { $crop.Save($Dst, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $crop.Dispose() }
    } finally { $img.Dispose() }
    return $Dst
}

# 对比两张图指定区域（默认整图）不同像素比例（0~1）。-Tolerance 为单通道允许误差
function Get-ImageDiffRatio {
    param([Parameter(Mandatory)][string]$PathA, [Parameter(Mandatory)][string]$PathB, [int[]]$Rect, [int]$Tolerance = 8)
    $a = [System.Drawing.Bitmap]::FromFile($PathA); $b = [System.Drawing.Bitmap]::FromFile($PathB)
    try {
        if (-not $Rect) { $Rect = @(0, 0, [Math]::Min($a.Width, $b.Width), [Math]::Min($a.Height, $b.Height)) }
        $r = New-Object System.Drawing.Rectangle $Rect[0], $Rect[1], $Rect[2], $Rect[3]
        $fmt = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
        $da = $a.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, $fmt)
        $db = $b.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, $fmt)
        $stride = $da.Stride
        try {
            $n = [Math]::Abs($stride) * $r.Height
            $ba = New-Object byte[] $n; $bb = New-Object byte[] $n
            [System.Runtime.InteropServices.Marshal]::Copy($da.Scan0, $ba, 0, $n)
            [System.Runtime.InteropServices.Marshal]::Copy($db.Scan0, $bb, 0, $n)
        } finally { $a.UnlockBits($da); $b.UnlockBits($db) }
        $diff = 0; $total = $r.Width * $r.Height
        for ($row = 0; $row -lt $r.Height; $row++) {
            $off = $row * $stride
            for ($col = 0; $col -lt $r.Width; $col++) {
                $i = $off + $col * 4
                if ([Math]::Abs([int]$ba[$i] - [int]$bb[$i]) -gt $Tolerance -or [Math]::Abs([int]$ba[$i+1] - [int]$bb[$i+1]) -gt $Tolerance -or [Math]::Abs([int]$ba[$i+2] - [int]$bb[$i+2]) -gt $Tolerance) { $diff++ }
            }
        }
        return [double]$diff / [double]$total
    } finally { $a.Dispose(); $b.Dispose() }
}

# ---------- 输入 ----------
function Wait-Ms { param([int]$Ms) Start-Sleep -Milliseconds $Ms }

$Script:MOUSE = @{ LeftDown = 0x2; LeftUp = 0x4; RightDown = 0x8; RightUp = 0x10 }

function Move-Mouse {
    param([Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y, [int]$Delay = 120)
    [void][DnTest.Native]::SetCursorPos($X, $Y)
    Wait-Ms $Delay
}

function Get-VkCode {
    param($Key)
    if ($Key -is [int]) { return [uint16]$Key }
    $map = @{
        'Enter'=0x0D; 'Esc'=0x1B; 'Escape'=0x1B; 'Tab'=0x09; 'Space'=0x20; 'Delete'=0x2E; 'Del'=0x2E; 'Backspace'=0x08;
        'Left'=0x25; 'Up'=0x26; 'Right'=0x27; 'Down'=0x28; 'Home'=0x24; 'End'=0x23; 'PageUp'=0x21; 'PageDown'=0x22;
        'Shift'=0x10; 'Ctrl'=0x11; 'Alt'=0x12; 'LWin'=0x5B; 'Apps'=0x5D
    }
    $s = [string]$Key
    if ($map.ContainsKey($s)) { return [uint16]$map[$s] }
    if ($s -match '^[Ff](\d{1,2})$') { return [uint16](0x6F + [int]$Matches[1]) }
    if ($s.Length -eq 1) { return [uint16][char]$s.ToUpper() }
    throw "未知按键：$Key"
}

function Click-Mouse {
    param([Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y,
          [ValidateSet('Left','Right')][string]$Button = 'Left', [switch]$Ctrl, [switch]$Shift, [int]$Delay = 150)
    Move-Mouse $X $Y 80
    if ($Ctrl)  { [DnTest.Native]::Key(0x11, 0, 0) }
    if ($Shift) { [DnTest.Native]::Key(0x10, 0, 0) }
    [DnTest.Native]::Mouse([uint32]$Script:MOUSE[$Button + 'Down']); Wait-Ms 40
    [DnTest.Native]::Mouse([uint32]$Script:MOUSE[$Button + 'Up'])
    if ($Shift) { [DnTest.Native]::Key(0x10, 0, 2) }
    if ($Ctrl)  { [DnTest.Native]::Key(0x11, 0, 2) }
    Wait-Ms $Delay
}

function DoubleClick-Mouse {
    param([Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y, [int]$Delay = 200)
    Move-Mouse $X $Y 80
    for ($i = 0; $i -lt 2; $i++) {
        [DnTest.Native]::Mouse(0x2); Wait-Ms 30
        [DnTest.Native]::Mouse(0x4); Wait-Ms 60
    }
    Wait-Ms $Delay
}

function Drag-Mouse {
    param([Parameter(Mandatory)][int]$X1, [Parameter(Mandatory)][int]$Y1, [Parameter(Mandatory)][int]$X2, [Parameter(Mandatory)][int]$Y2,
          [int]$Steps = 20, [ValidateSet('Left','Right')][string]$Button = 'Left', [int]$StepDelay = 15, [int]$Delay = 200)
    Move-Mouse $X1 $Y1 100
    [DnTest.Native]::Mouse([uint32]$Script:MOUSE[$Button + 'Down']); Wait-Ms 80
    for ($i = 1; $i -le $Steps; $i++) {
        [void][DnTest.Native]::SetCursorPos([int]($X1 + ($X2 - $X1) * $i / $Steps), [int]($Y1 + ($Y2 - $Y1) * $i / $Steps))
        Wait-Ms $StepDelay
    }
    Wait-Ms 120
    [DnTest.Native]::Mouse([uint32]$Script:MOUSE[$Button + 'Up'])
    Wait-Ms $Delay
}

# 按键：Key 可为 VK 数值或名称（Enter/F2/Delete/A…）
function Press-Key {
    param([Parameter(Mandatory)]$Key, [switch]$Ctrl, [switch]$Shift, [switch]$Alt, [int]$Delay = 150)
    $vk = Get-VkCode $Key
    if ($Ctrl)  { [DnTest.Native]::Key(0x11, 0, 0) }
    if ($Shift) { [DnTest.Native]::Key(0x10, 0, 0) }
    if ($Alt)   { [DnTest.Native]::Key(0x12, 0, 0) }
    [DnTest.Native]::Key($vk, 0, 0); Wait-Ms 30
    [DnTest.Native]::Key($vk, 0, 2)
    if ($Alt)   { [DnTest.Native]::Key(0x12, 0, 2) }
    if ($Shift) { [DnTest.Native]::Key(0x10, 0, 2) }
    if ($Ctrl)  { [DnTest.Native]::Key(0x11, 0, 2) }
    Wait-Ms $Delay
}
Set-Alias Send-Keys Press-Key

# 键入文本（KEYEVENTF_UNICODE = 4，KEYUP = 2）
function Type-Text {
    param([Parameter(Mandatory)][string]$Text, [int]$Delay = 150)
    foreach ($c in $Text.ToCharArray()) {
        [DnTest.Native]::Key(0, [uint16][char]$c, 4)
        [DnTest.Native]::Key(0, [uint16][char]$c, 6)
        Wait-Ms 20
    }
    Wait-Ms $Delay
}

function Send-WinD {
    param([int]$Delay = 800)
    [DnTest.Native]::keybd_event(0x5B, 0, 0, [UIntPtr]::Zero)
    [DnTest.Native]::keybd_event(0x44, 0, 0, [UIntPtr]::Zero)
    [DnTest.Native]::keybd_event(0x44, 0, 2, [UIntPtr]::Zero)
    [DnTest.Native]::keybd_event(0x5B, 0, 2, [UIntPtr]::Zero)
    Wait-Ms $Delay
}

# ---------- 窗口 ----------
function Minimize-All { (New-Object -ComObject Shell.Application).MinimizeAll(); Wait-Ms 1200 }
function Restore-All  { (New-Object -ComObject Shell.Application).UndoMinimizeALL(); Wait-Ms 800 }
function Get-ForegroundTitle { return [DnTest.Native]::FgTitle() }
function Stop-ProcessByName {
    param([Parameter(Mandatory)][string]$Name)
    Get-Process -Name $Name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

# ---------- 日志 ----------
function Get-LogLines {
    if (-not (Test-Path $Script:LogPath)) { return @() }
    $fs = New-Object System.IO.FileStream($Script:LogPath, 'Open', 'Read', 'ReadWrite')
    try {
        $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
        $lines = New-Object System.Collections.Generic.List[string]
        while (($l = $sr.ReadLine()) -ne $null) { $lines.Add($l) }
        return ,$lines.ToArray()
    } finally { $fs.Dispose() }
}
# 当前日志字节数，用作 Wait-Log 的 -Since（按字节偏移读取，日志很大时也快）
function Get-LogMark {
    if (-not (Test-Path $Script:LogPath)) { return 0 }
    return [int64](Get-Item $Script:LogPath).Length
}

# 读取 Since（字节偏移）之后的日志行
function Get-LogSince {
    param([int64]$Since = 0)
    if (-not (Test-Path $Script:LogPath)) { return @() }
    $fs = New-Object System.IO.FileStream($Script:LogPath, 'Open', 'Read', 'ReadWrite')
    try {
        if ($Since -gt $fs.Length) { $Since = 0 }
        [void]$fs.Seek($Since, 'Begin')
        $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
        $lines = New-Object System.Collections.Generic.List[string]
        while (($l = $sr.ReadLine()) -ne $null) { $lines.Add($l) }
        return ,$lines.ToArray()
    } finally { $fs.Dispose() }
}

# 等待 Since（Get-LogMark 的值）之后出现匹配 Pattern（正则）的日志行，返回该行；超时返回 $null
function Wait-Log {
    param([Parameter(Mandatory)][string]$Pattern, [int64]$Since = 0, [int]$TimeoutSec = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        foreach ($l in @(Get-LogSince $Since)) { if ($l -match $Pattern) { return $l } }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    return $null
}

# ---------- 应用生命周期 ----------
# 返回启动前的日志行数（可作为 Wait-Log 的 Since）
function Start-DeskNook {
    param([string[]]$AppArgs = @(), [int]$TimeoutSec = 20)
    if (-not (Test-Path $Script:ExePath)) { throw "找不到 $Script:ExePath，请先 dotnet build -c Release" }
    $mark = Get-LogMark
    if ($AppArgs.Count -gt 0) { Start-Process -FilePath $Script:ExePath -ArgumentList $AppArgs | Out-Null }
    else { Start-Process -FilePath $Script:ExePath | Out-Null }
    $hit = Wait-Log -Pattern '已隐藏系统桌面图标' -Since $mark -TimeoutSec $TimeoutSec
    if (-not $hit) { throw '启动超时：未在日志中看到“已隐藏系统桌面图标”' }
    Start-Sleep -Milliseconds 800
    return $mark
}

function Stop-DeskNook {
    param([int]$TimeoutSec = 10)
    if (-not (Get-Process -Name DeskNook -ErrorAction SilentlyContinue)) { return $true }
    if (Test-Path $Script:ExePath) { Start-Process -FilePath $Script:ExePath -ArgumentList '--exit' | Out-Null }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name DeskNook -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
    if (Get-Process -Name DeskNook -ErrorAction SilentlyContinue) {
        Write-Warning 'DeskNook 未响应 --exit，强制结束并兜底恢复系统图标'
        Stop-ProcessByName DeskNook
        Start-Sleep -Milliseconds 300
        [void][DnTest.Native]::ShowSystemIcons()
        return $false
    }
    return $true
}

# ---------- 测试文件（仅 xk-test- 前缀） ----------
function New-TestFile {
    param([Parameter(Mandatory)][string]$Name, [string]$Content = 'xk-test', [switch]$Directory)
    if (-not $Name.StartsWith('xk-test-')) { throw "测试文件名必须以 xk-test- 开头：$Name" }
    if ($Name -match '[\\/]') { throw "测试文件名不能含路径分隔符：$Name" }
    $p = Join-Path $Script:DesktopDir $Name
    if ($Directory) { New-Item -ItemType Directory -Path $p -Force | Out-Null }
    else { [System.IO.File]::WriteAllText($p, $Content, (New-Object System.Text.UTF8Encoding($false))) }
    return $p
}

# 只删除桌面上 xk-test-* 项
function Remove-TestFiles {
    Get-ChildItem -LiteralPath $Script:DesktopDir -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'xk-test-*' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

# ---------- 断言/报告 ----------
function Write-Result {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][bool]$Pass, [string]$Detail = '')
    [void]$Script:Results.Add([pscustomobject]@{ Name = $Name; Pass = $Pass; Detail = $Detail })
    $tag = if ($Pass) { 'PASS' } else { 'FAIL' }
    $color = if ($Pass) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1} {2}" -f $tag, $Name, $Detail) -ForegroundColor $color
}

function Assert-True {
    param($Cond, [string]$Msg = '断言失败')
    if (-not $Cond) { throw $Msg }
}

function Invoke-Test {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Script)
    try {
        $out = & $Script
        Write-Result $Name $true ([string]($out | Select-Object -Last 1))
    } catch {
        $shot = ''
        try { $shot = Save-Screen -Name ("fail-" + ($Name -replace '[^\w\-]', '_')) } catch { }
        Write-Result $Name $false ("{0} 截图={1}" -f $_.Exception.Message, $shot)
    }
}

function Show-Summary {
    $fail = @($Script:Results | Where-Object { -not $_.Pass })
    Write-Host ("==== 汇总：共 {0} 项，通过 {1}，失败 {2} ====" -f $Script:Results.Count, ($Script:Results.Count - $fail.Count), $fail.Count)
    foreach ($f in $fail) { Write-Host ("  失败：{0} {1}" -f $f.Name, $f.Detail) -ForegroundColor Red }
    return $fail.Count
}
