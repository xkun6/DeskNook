# 阶段 4（双击隐藏/托盘/自启/设置/菜单图标）自动化测试公共库。输出文件前缀 h-。
. $PSScriptRoot\m-lib.ps1

$Script:HBackupDir = Join-Path $env:TEMP 'xk-h-backup'
$Script:RunKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

# ---------- 备份/恢复：程序目录 data\ 下所有 json + Run\XkDesk 注册表值 ----------
function Backup-HState {
    if (Test-Path $Script:HBackupDir) { return }  # 上次中断遗留的备份不覆盖
    New-Item -ItemType Directory -Path $Script:HBackupDir | Out-Null
    foreach ($f in Get-ChildItem -LiteralPath $Script:AppDataDir -Filter *.json -ErrorAction SilentlyContinue) {
        Copy-Item $f.FullName (Join-Path $Script:HBackupDir $f.Name) -Force
    }
    $v = (Get-ItemProperty -Path $Script:RunKeyPath -Name XkDesk -ErrorAction SilentlyContinue).XkDesk
    if ($null -ne $v) { Set-Content -Path (Join-Path $Script:HBackupDir 'run.value') -Value $v -Encoding UTF8 }
    else { Set-Content -Path (Join-Path $Script:HBackupDir 'run.missing') -Value '' }
}
function Restore-HState {
    if (-not (Test-Path $Script:HBackupDir)) { return }
    foreach ($f in Get-ChildItem -LiteralPath $Script:AppDataDir -Filter *.json -ErrorAction SilentlyContinue) {
        if (-not (Test-Path (Join-Path $Script:HBackupDir $f.Name))) { Remove-Item $f.FullName -Force }
    }
    foreach ($f in Get-ChildItem -LiteralPath $Script:HBackupDir -Filter *.json) {
        Copy-Item $f.FullName (Join-Path $Script:AppDataDir $f.Name) -Force
    }
    if (Test-Path (Join-Path $Script:HBackupDir 'run.value')) {
        $v = (Get-Content (Join-Path $Script:HBackupDir 'run.value') -Raw -Encoding UTF8).TrimEnd("`r", "`n")
        Set-ItemProperty -Path $Script:RunKeyPath -Name XkDesk -Value $v
    } else {
        Remove-ItemProperty -Path $Script:RunKeyPath -Name XkDesk -ErrorAction SilentlyContinue
    }
    Remove-Item $Script:HBackupDir -Recurse -Force -ErrorAction SilentlyContinue
}
function Get-RunValue { return (Get-ItemProperty -Path $Script:RunKeyPath -Name XkDesk -ErrorAction SilentlyContinue).XkDesk }

function Read-Json { param([string]$Name) return (Get-Content (Join-Path $Script:AppDataDir $Name) -Raw -Encoding UTF8 | ConvertFrom-Json) }

# 设置 settings.json（只写我们关心的字段；其余取默认）
function Write-Settings { param([hashtable]$Fields)
    $p = Join-Path $Script:AppDataDir 'settings.json'
    ($Fields | ConvertTo-Json -Depth 5) | Set-Content -Path $p -Encoding UTF8
}

# 窗口：按类名/标题找
function Find-WindowByTitle { param([string]$Title) return [XkTest.Native]::FindWindow($null, $Title) }

# ---------- 托盘（UI Automation 定位通知区域图标） ----------
if (-not ('XkTest.Tray' -as [type])) {
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
}

# 返回托盘区（含溢出区）名称包含 Name 的按钮的矩形中心；找不到返回 $null。-Overflow：打开溢出区后再找
function Find-TrayButton {
    param([string]$Name = 'xk-desk')
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Button)
    foreach ($wcls in 'Shell_TrayWnd', 'NotifyIconOverflowWindow') {
        $wc = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ClassNameProperty), $wcls
        $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $wc)
        if (-not $w) { continue }
        foreach ($b in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            if ($b.Current.Name -like "*$Name*" -and -not $b.Current.IsOffscreen) {
                $r = $b.Current.BoundingRectangle
                if ($r.Width -gt 0) { return @{ X = [int]($r.X + $r.Width / 2); Y = [int]($r.Y + $r.Height / 2); Window = $wcls; Rect = $r } }
            }
        }
    }
    return $null
}

# ---------- UI Automation：窗口与控件 ----------
function Get-UiaWindow {
    param([Parameter(Mandatory)][string]$Title, [int]$TimeoutMs = 5000)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $Title
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    do {
        $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return $null
}
function Get-UiaById {
    param($Window, [Parameter(Mandatory)][string]$Id)
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), $Id
    return $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function Get-UiaCenter {
    param($El)
    $r = $El.Current.BoundingRectangle
    return @{ X = [int]($r.X + $r.Width / 2); Y = [int]($r.Y + $r.Height / 2) }
}
function Click-Uia { param($El, [int]$Delay = 300) $c = Get-UiaCenter $El; Click-Mouse $c.X $c.Y -Delay $Delay }
function Get-WindowRect { param($Win) $r = $Win.Current.BoundingRectangle; return @([int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height) }
function Close-SettingsWindow {
    $w = Get-UiaWindow 'xk-desk 设置' 1000
    if ($w) { Click-Uia (Get-UiaById $w 'BtnCancel') 500 }
}

# 打开托盘溢出区（图标被折叠时），返回是否点击了 chevron
function Open-TrayOverflow {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $wc = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ClassNameProperty), 'Shell_TrayWnd'
    $tray = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $wc)
    if (-not $tray) { return $false }
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Button)
    foreach ($b in $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($b.Current.Name -like '*隐藏的图标*' -or $b.Current.Name -like '*Show Hidden*') { Click-Uia $b 700; return $true }
    }
    return $false
}

# 定位 xk-desk 托盘图标（先看任务栏，再开溢出区）；返回 @{X;Y;Window}，找不到返回 $null
function Locate-TrayIcon {
    $t = Find-TrayButton 'xk-desk'
    if ($t) { return $t }
    if (Open-TrayOverflow) { $t = Find-TrayButton 'xk-desk' }
    return $t
}
function Close-TrayOverflow { Press-Key Escape -Delay 200 }

# 右键托盘图标，等菜单出现，返回菜单项文本数组
function Open-TrayMenu {
    $t = Locate-TrayIcon
    if (-not $t) { throw '找不到 xk-desk 托盘图标' }
    Click-Mouse $t.X $t.Y -Button Right -Delay 800
    return @(Get-MenuTexts)
}

# 点击托盘菜单项（菜单已弹出时用）
function Click-TrayMenuItem {
    param([string]$Text, [int]$Delay = 700)
    $m = Find-MenuItem $Text -TimeoutMs 3000
    if (-not $m) { Press-Key Escape; throw "托盘菜单找不到：$Text（当前：$((Get-MenuTexts) -join ' | ')）" }
    Click-Mouse $m.X $m.Y -Delay $Delay
}

function Wait-Layout2 { param([scriptblock]$Cond, [int]$TimeoutSec = 6) return (Wait-Layout $Cond $TimeoutSec) }

# 两张截图指定矩形的差异比例
function Rect-Diff { param([string]$A, [string]$B, [int[]]$Rect) return (Get-ImageDiffRatio -PathA $A -PathB $B -Rect $Rect -Tolerance 10) }

# ---------- 托盘定位：Shell_NotifyIconGetRect（hWnd = XkDesk 消息窗口，uID = 1）----------
if (-not ('TrayRect' -as [type])) {
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class TrayRect {
  [StructLayout(LayoutKind.Sequential)] public struct NII { public int cbSize; public IntPtr hWnd; public uint uID; public Guid g; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NII id, out RECT r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string c, string t);
  // 返回 int[]{L,T,R,B}；失败返回 null
  public static int[] Get() {
    IntPtr h = FindWindow(null, "XkDeskMessageWindow");
    if (h == IntPtr.Zero) return null;
    NII n = new NII(); n.cbSize = Marshal.SizeOf(typeof(NII)); n.hWnd = h; n.uID = 1; RECT r;
    if (Shell_NotifyIconGetRect(ref n, out r) < 0) return null;
    return new int[] { r.L, r.T, r.R, r.B };
  }
}
'@
}

function Locate-TrayIcon {
    $r = [TrayRect]::Get()
    if (-not $r -or ($r[2] - $r[0]) -le 0) { return $null }
    return @{ X = [int](($r[0] + $r[2]) / 2); Y = [int](($r[1] + $r[3]) / 2); Window = 'Shell_NotifyIconGetRect'; Rect = $r }
}

# 托盘折叠按钮（chevron）的矩形；找不到返回 $null
function Get-TrayChevronRect {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $wc = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ClassNameProperty), 'Shell_TrayWnd'
    $tray = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $wc)
    if (-not $tray) { return $null }
    $cc = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ClassNameProperty), 'Button'
    $b = $tray.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cc)
    if (-not $b) { return $null }
    return $b.Current.BoundingRectangle
}

# 定位 xk-desk 托盘图标：图标被折叠在溢出区时，Shell_NotifyIconGetRect 返回折叠按钮的位置，
# 此时先点击折叠按钮展开溢出区，再重新取图标的矩形（返回的点可直接点击）。
function Locate-TrayIcon {
    $r = [TrayRect]::Get()
    if (-not $r -or ($r[2] - $r[0]) -le 0) { return $null }
    $cx = [int](($r[0] + $r[2]) / 2); $cy = [int](($r[1] + $r[3]) / 2)
    $chev = Get-TrayChevronRect
    if ($chev -and $cx -ge $chev.X -and $cx -le ($chev.X + $chev.Width) -and $cy -ge $chev.Y -and $cy -le ($chev.Y + $chev.Height)) {
        Click-Mouse $cx $cy -Delay 900
        $r = [TrayRect]::Get()
        if (-not $r) { return $null }
        $cx = [int](($r[0] + $r[2]) / 2); $cy = [int](($r[1] + $r[3]) / 2)
    }
    return @{ X = $cx; Y = $cy; Window = 'Shell_NotifyIconGetRect'; Rect = $r }
}
