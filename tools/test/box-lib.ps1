# 阶段 2（格子）自动化测试公共辅助：布局备份/恢复、读取 layout.json 里的格子与图标位置、菜单点击。
# 主显示器 100% 缩放，工作区原点 (0,0)，格子 75x100 DIP，图标 48。
. $PSScriptRoot\common.ps1

$Script:LayoutBackup = Join-Path $env:TEMP 'xk-layout-backup-stage2.json'
$Script:MapDir = Join-Path $env:TEMP 'xk-test-map'
$Script:TitleH = 32
$Script:ChromeH = 36
$Script:CW = 75
$Script:CH = 100

# 备份真实布局（只在尚无备份时备份，防止中途失败后二次覆盖掉真正的备份）
function Backup-Layout {
    if (Test-Path $Script:LayoutBackup) { return }
    if (Test-Path $Script:LayoutPath) { Copy-Item $Script:LayoutPath $Script:LayoutBackup -Force }
    else { Set-Content -Path $Script:LayoutBackup -Value '__NOFILE__' }
}

function Restore-Layout {
    if (-not (Test-Path $Script:LayoutBackup)) { return }
    $c = Get-Content $Script:LayoutBackup -Raw
    if ($c.Trim() -eq '__NOFILE__') { Remove-Item $Script:LayoutPath -Force -ErrorAction SilentlyContinue }
    else { Copy-Item $Script:LayoutBackup $Script:LayoutPath -Force }
    Remove-Item $Script:LayoutBackup -Force -ErrorAction SilentlyContinue
}

function Read-Layout { return (Get-Content $Script:LayoutPath -Raw -Encoding UTF8 | ConvertFrom-Json) }

# 程序保存有 500ms 去抖，等落盘
function Wait-Saved { Wait-Ms 900 }

function Get-Boxes { $l = Read-Layout; return @($l.Boxes) }
function Get-BoxByName { param([string]$Name) return (Get-Boxes | Where-Object { $_.Name -eq $Name } | Select-Object -First 1) }

# 格子在屏幕上的矩形（主显示器，物理像素=DIP）
function Get-BoxScreenRect {
    param($Box)
    return @{ X = [int]$Box.Rect.X; Y = [int]$Box.Rect.Y; W = [int]$Box.Rect.W; H = [int]$Box.Rect.H }
}

# 格子内第 index 个图标中心（假设内容区顶部=格子 Y+TitleH，无滚动，列数=W/CW）
function Get-BoxIconCenter {
    param($Box, [int]$Index)
    $cols = [Math]::Max(1, [int][Math]::Floor($Box.Rect.W / $Script:CW))
    $c = $Index % $cols; $r = [Math]::Floor($Index / $cols)
    return @{ X = [int]($Box.Rect.X + $c * $Script:CW + 37); Y = [int]($Box.Rect.Y + $Script:TitleH + $r * $Script:CH + 28) }
}

if (-not ('DnTest.MenuApi' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace DnTest {
  public static class MenuApi {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr m);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetMenuString(IntPtr m, uint pos, StringBuilder sb, int max, uint flags);
    [DllImport("user32.dll")] static extern bool GetMenuItemRect(IntPtr hwnd, IntPtr m, uint pos, out RECT r);
    [DllImport("user32.dll")] static extern uint GetMenuState(IntPtr m, uint pos, uint flags);
    // 返回所有可见弹出菜单里的项："文本|X|Y|宽|高|状态"
    public static string[] Items() {
      List<string> res = new List<string>();
      EnumWindows(delegate(IntPtr h, IntPtr l) {
        StringBuilder cn = new StringBuilder(64); GetClassName(h, cn, 64);
        if (cn.ToString() != "#32768" || !IsWindowVisible(h)) return true;
        IntPtr hm = SendMessage(h, 0x01E1, IntPtr.Zero, IntPtr.Zero);
        if (hm == IntPtr.Zero) return true;
        int n = GetMenuItemCount(hm);
        for (int i = 0; i < n; i++) {
          StringBuilder sb = new StringBuilder(256); GetMenuString(hm, (uint)i, sb, 256, 0x400);
          RECT r; if (!GetMenuItemRect(IntPtr.Zero, hm, (uint)i, out r)) continue;
          uint st = GetMenuState(hm, (uint)i, 0x400);
          res.Add(sb.ToString() + "|" + r.Left + "|" + r.Top + "|" + (r.Right - r.Left) + "|" + (r.Bottom - r.Top) + "|" + st);
        }
        return true;
      }, IntPtr.Zero);
      return res.ToArray();
    }
  }
}
'@
}

# 弹出的菜单里找菜单项（遍历所有可见 #32768 菜单窗口的 HMENU；菜单由 explorer.exe 弹出时同样可读），返回中心点；找不到返回 $null。
# 匹配：先找文本完全相等的项（去掉 &、(&X) 后），找不到再按通配符包含匹配（如 '新建映射格子' 可匹配 '新建映射格子…'）；-Exact 只做完全相等。
function Find-MenuItem {
    param([Parameter(Mandatory)][string]$Text, [int]$TimeoutMs = 3000, [switch]$Exact)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    do {
        $exactHit = $null; $likeHit = $null
        foreach ($line in [DnTest.MenuApi]::Items()) {
            $p = $line.Split('|')
            $name = ($p[0] -replace '\(&.\)|&', '').Trim()
            if (-not $name -or [int]$p[3] -le 0) { continue }
            $hit = @{ X = [int]$p[1] + [int]([int]$p[3] / 2); Y = [int]$p[2] + [int]([int]$p[4] / 2); Name = $p[0]; State = [int64]$p[5] }
            if ($name -eq $Text) { if (-not $exactHit) { $exactHit = $hit } }
            elseif (-not $Exact -and $name -like "*$Text*") { if (-not $likeHit) { $likeHit = $hit } }
        }
        if ($exactHit) { return $exactHit }
        if ($likeHit) { return $likeHit }
        Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $deadline)
    return $null
}

# 当前所有弹出菜单项文本（调试/断言用）
function Get-MenuTexts { return @([DnTest.MenuApi]::Items() | ForEach-Object { ($_.Split('|')[0] -replace '\(&.\)|&', '').Trim() }) }

# 路径式菜单选择：Path 为数组（@('桌面整理','新建格子')）或用 ▸ 分隔的字符串（'桌面整理 ▸ 新建格子'）；
# 在已弹出的菜单里依次点击各级（父级点击即展开子菜单）。统一入口，适用于 explorer.exe 弹出的菜单和 DeskNook 自己弹出的菜单。
function Split-MenuPath {
    param([Parameter(Mandatory)][string[]]$Path)
    return @($Path | ForEach-Object { $_ -split '\s*▸\s*' } | Where-Object { $_ -ne '' })
}
function Click-MenuPath {
    param([Parameter(Mandatory)][string[]]$Path, [int]$SettleMs = 500)
    foreach ($seg in (Split-MenuPath $Path)) {
        $m = Find-MenuItem $seg -TimeoutMs 4000
        if (-not $m) { Press-Key Escape; Press-Key Escape; throw "找不到菜单项：$seg（当前：$((Get-MenuTexts) -join ' | ')）" }
        Click-Mouse $m.X $m.Y -Delay $SettleMs
    }
}

# 右键点 (x,y) → 按路径点击菜单项
function Click-ContextMenu {
    param([int]$X, [int]$Y, [Parameter(Mandatory)][string[]]$Path, [int]$SettleMs = 700)
    Click-Mouse $X $Y -Button Right; Wait-Ms $SettleMs
    Click-MenuPath $Path
}

function Test-Free { return $true }

# 清理所有测试痕迹（桌面 xk-test-*、映射目录）
function Clear-TestArtifacts {
    Remove-TestFiles
    if (Test-Path $Script:MapDir) { Remove-Item $Script:MapDir -Recurse -Force -ErrorAction SilentlyContinue }
}

if (-not ('DnTest.Wheel' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace DnTest {
  public static class Wheel {
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    public static void Scroll(int delta) { mouse_event(0x0800, 0, 0, delta, UIntPtr.Zero); }
  }
}
'@
}

# 在 (x,y) 处滚轮（delta 正 = 向上，负 = 向下；一格 120）
function Scroll-Wheel {
    param([int]$X, [int]$Y, [int]$Delta, [int]$Delay = 400)
    Move-Mouse $X $Y 120
    [DnTest.Wheel]::Scroll($Delta); Wait-Ms $Delay
}

# 拖动过程中截图：按下 → 逐步移动到目标 → 截图（保持按下）→ 释放
function Drag-Mouse-Shot {
    param([int]$X1, [int]$Y1, [int]$X2, [int]$Y2, [string]$ShotName, [int[]]$ShotRect, [int]$Steps = 20)
    Move-Mouse $X1 $Y1 100
    [DnTest.Native]::Mouse(0x2); Wait-Ms 80
    for ($i = 1; $i -le $Steps; $i++) {
        [void][DnTest.Native]::SetCursorPos([int]($X1 + ($X2 - $X1) * $i / $Steps), [int]($Y1 + ($Y2 - $Y1) * $i / $Steps))
        Wait-Ms 15
    }
    Wait-Ms 300
    if ($ShotRect) { Save-Screen -Name $ShotName -Rect $ShotRect | Out-Null } else { Save-Screen -Name $ShotName | Out-Null }
    [DnTest.Native]::Mouse(0x4); Wait-Ms 500
}

# ---------- 测试生命周期 ----------
function Start-BoxTest {
    param([string[]]$Files = @('a', 'b', 'c'), [switch]$NoStart)
    Stop-DeskNook | Out-Null
    Backup-Layout
    Remove-Item $Script:LayoutPath -Force -ErrorAction SilentlyContinue   # 从空布局开始（原布局已备份，结束时还原）
    Clear-TestArtifacts
    foreach ($f in $Files) { New-TestFile -Name "xk-test-$f.txt" -Content "xk-test $f" | Out-Null }
    Wait-Ms 1000
    Minimize-All
    if (-not $NoStart) { Start-DeskNook | Out-Null; Wait-Ms 1500; Wait-Saved }
}

function Finish-BoxTest {
    try { Press-Key Escape } catch { }
    Stop-DeskNook | Out-Null
    Wait-Ms 800
    Clear-TestArtifacts
    Restore-Layout
    try { Restore-All } catch { }
    Stop-ProcessByName notepad
}

# 等待布局里满足条件（轮询 layout.json）
function Wait-Layout {
    param([Parameter(Mandatory)][scriptblock]$Cond, [int]$TimeoutSec = 6)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        try { if (& $Cond (Read-Layout)) { return $true } } catch { }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Key-Path { param([string]$Name) return (Join-Path $Script:DesktopDir $Name) }
