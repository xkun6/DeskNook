# 右键菜单 v2（Explorer 内代理 + Shell 扩展）自动化测试公共库。输出文件前缀 m-。
. $PSScriptRoot\box-lib.ps1

$Script:AppDataDir = $Script:DataDir   # 程序目录下的 data（lib.ps1 定义）
$Script:MBackupDir = Join-Path $env:TEMP 'xk-m-backup'
$Script:ShellLogPath = Join-Path $Script:DataDir 'logs\shellext.log'
$Script:AppDataFiles = @('layout.json', 'settings.json', 'organize-undo.json')

if (-not ('DnTest.Ext' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace DnTest {
  public static class Ext {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
    public delegate bool EP(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EP cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static uint ShellPid() { uint pid; IntPtr pm = FindWindow("Progman", null); if (pm == IntPtr.Zero) return 0; GetWindowThreadProcessId(pm, out pid); return pid; }
    // 系统桌面 SysListView32 是否可见（DeskNext 运行时应一直隐藏）
    public static bool SystemListViewVisible() {
      IntPtr pm = FindWindow("Progman", null);
      IntPtr dv = pm == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(pm, IntPtr.Zero, "SHELLDLL_DefView", null);
      bool found = false; bool visible = false;
      if (dv != IntPtr.Zero) { IntPtr lv = FindWindowEx(dv, IntPtr.Zero, "SysListView32", null); if (lv != IntPtr.Zero) { found = true; visible = IsWindowVisible(lv); } }
      if (!found) {
        EnumWindows(delegate(IntPtr h, IntPtr l) {
          System.Text.StringBuilder sb = new System.Text.StringBuilder(64); GetClassName(h, sb, 64);
          if (sb.ToString() == "WorkerW") {
            IntPtr d = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (d != IntPtr.Zero) { IntPtr lv2 = FindWindowEx(d, IntPtr.Zero, "SysListView32", null); if (lv2 != IntPtr.Zero) { found = true; visible = IsWindowVisible(lv2); return false; } }
          }
          return true;
        }, IntPtr.Zero);
      }
      return visible;
    }
  }
}
'@
}

# ---------- AppData 备份/恢复（layout.json / settings.json / organize-undo.json）----------
function Backup-AppData {
    if (Test-Path $Script:MBackupDir) { return }  # 已有备份（上次中断）时不覆盖
    New-Item -ItemType Directory -Path $Script:MBackupDir | Out-Null
    foreach ($f in $Script:AppDataFiles) {
        $src = Join-Path $Script:AppDataDir $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $Script:MBackupDir $f) -Force }
        else { Set-Content -Path (Join-Path $Script:MBackupDir ($f + '.missing')) -Value '' }
    }
}
function Restore-AppData {
    if (-not (Test-Path $Script:MBackupDir)) { return }
    foreach ($f in $Script:AppDataFiles) {
        $dst = Join-Path $Script:AppDataDir $f
        $bak = Join-Path $Script:MBackupDir $f
        if (Test-Path $bak) { Copy-Item $bak $dst -Force }
        elseif (Test-Path ($bak + '.missing')) { Remove-Item $dst -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item $Script:MBackupDir -Recurse -Force -ErrorAction SilentlyContinue
}
function Test-AppDataRestored {
    # 与备份一致（备份已被删除时不可比较，返回 $true）
    return -not (Test-Path $Script:MBackupDir)
}

# ---------- 日志读取（任意文件，按字节偏移）----------
function Get-FileMark { param([string]$Path) if (-not (Test-Path $Path)) { return [int64]0 } return [int64](Get-Item $Path).Length }
function Get-FileSince {
    param([string]$Path, [int64]$Since = 0)
    if (-not (Test-Path $Path)) { return @() }
    $fs = New-Object System.IO.FileStream($Path, 'Open', 'Read', 'ReadWrite')
    try {
        if ($Since -gt $fs.Length) { $Since = 0 }
        [void]$fs.Seek($Since, 'Begin')
        $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
        $lines = New-Object System.Collections.Generic.List[string]
        while (($l = $sr.ReadLine()) -ne $null) { $lines.Add($l) }
        return ,$lines.ToArray()
    } finally { $fs.Dispose() }
}
function Wait-FileLog {
    param([string]$Path, [string]$Pattern, [int64]$Since, [int]$TimeoutSec = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        foreach ($l in @(Get-FileSince $Path $Since)) { if ($l -match $Pattern) { return $l } }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    return $null
}

# ---------- Explorer ----------
function Get-ExplorerPids { return @([DnTest.Ext]::ShellPid()) }
function Restart-Explorer {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1500
    Start-Process explorer.exe | Out-Null
}

# ---------- 菜单 ----------
$Script:OurItems = @('整理至新格子', '整理至新文件夹', '作为桌面格子显示', '打开所在位置', '一键整理', '桌面整理', '移动到格子', '移出格子')

# 右键 (x,y)，等待菜单出现，截图并返回菜单顶层文本；Esc 关闭。返回 @{ Texts; Shot; Ms }
function Capture-Menu {
    param([Parameter(Mandatory)][string]$Name, [int]$X, [int]$Y, [switch]$Keep, [string]$WaitFor = '')
    $t0 = Get-Date
    Click-Mouse $X $Y -Button Right -Delay 50
    $deadline = (Get-Date).AddSeconds(4)
    $texts = @()
    do {
        $texts = @(Get-MenuTexts)
        if ($texts.Count -gt 0 -and ($WaitFor -eq '' -or ($texts -contains $WaitFor))) { break }
        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)
    $ms = [int]((Get-Date) - $t0).TotalMilliseconds
    Wait-Ms 600  # 等扩展图标与异步项到位
    $texts = @(Get-MenuTexts)
    $shot = Save-Screen -Name $Name
    Set-Content -Path (Join-Path $Script:OutDir ($Name + '.txt')) -Value $texts -Encoding UTF8
    if (-not $Keep) { Press-Key Escape -Delay 200; Press-Key Escape -Delay 300 }
    return @{ Texts = $texts; Shot = $shot; Ms = $ms }
}

# 去掉我们的项、折叠连续分隔线，便于与原生菜单比对
function Normalize-MenuTexts {
    param([string[]]$Texts)
    $out = New-Object System.Collections.ArrayList
    foreach ($t in $Texts) {
        $n = ($t -replace '\t.*$', '').Trim()
        if ($Script:OurItems -contains $n) { continue }
        if ($n -eq '' -and ($out.Count -eq 0 -or $out[$out.Count - 1] -eq '')) { continue }
        [void]$out.Add($n)
    }
    while ($out.Count -gt 0 -and $out[$out.Count - 1] -eq '') { $out.RemoveAt($out.Count - 1) }
    return ,@($out.ToArray())
}

# 左右拼接两张截图的同一区域，便于对比
function New-SideBySide {
    param([string]$Left, [string]$Right, [string]$Dst, [int]$X, [int]$Y, [int]$W, [int]$H, [string]$LabelL = '', [string]$LabelR = '')
    $a = [System.Drawing.Bitmap]::FromFile($Left); $b = [System.Drawing.Bitmap]::FromFile($Right)
    try {
        $bmp = New-Object System.Drawing.Bitmap (($W * 2 + 10), ($H + 24))
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::Black)
        $rect = New-Object System.Drawing.Rectangle $X, $Y, $W, $H
        $g.DrawImage($a, (New-Object System.Drawing.Rectangle 0, 24, $W, $H), $rect, [System.Drawing.GraphicsUnit]::Pixel)
        $g.DrawImage($b, (New-Object System.Drawing.Rectangle ($W + 10), 24, $W, $H), $rect, [System.Drawing.GraphicsUnit]::Pixel)
        $f = New-Object System.Drawing.Font 'Segoe UI', 11
        $g.DrawString($LabelL, $f, [System.Drawing.Brushes]::White, 4, 2)
        $g.DrawString($LabelR, $f, [System.Drawing.Brushes]::White, ($W + 14), 2)
        $g.Dispose()
        $p = Join-Path $Script:OutDir $Dst
        $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        return $p
    } finally { $a.Dispose(); $b.Dispose() }
}

function Get-BoxCount { return @((Read-Layout).Boxes).Count }

# 清理测试创建的“新建文件夹*”（仅当其中全是 xk-test- 项）
function Clear-NewFolders {
    Get-ChildItem -LiteralPath $Script:DesktopDir -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '新建文件夹*' } | ForEach-Object {
        $kids = @(Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue)
        if ($kids.Count -eq 0 -or @($kids | Where-Object { $_.Name -notlike 'xk-test-*' }).Count -eq 0) { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Close-ForegroundWindowAltF4 { Press-Key F4 -Alt -Delay 600 }
