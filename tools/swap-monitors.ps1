# 对调 layout.json 中两块显示器的格子与自由图标（含图标 Home 里的显示器）。
# 用途：双屏内容被整体调换后，不用手动拖回，一键把两块屏的 Monitor 值互换。
# 流程：定位数据目录 -> 退出对应的 DeskNook -> 备份 layout.json -> 按文本对调 Monitor 值 -> 重新启动 DeskNook。
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/swap-monitors.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/swap-monitors.ps1 -DataDir D:\DeskNook\data -NoRestart
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/swap-monitors.ps1 -A '\\\\.\\DISPLAY1' -B '\\\\.\\DISPLAY2'
# 参数：
#   -DataDir    数据根目录（含 layout.json）。省略时从运行中的 DeskNook 进程推导：
#               <exe 目录>\data 存在且含 layout.json 就用它，否则用 %AppData%\DeskNook。
#   -A / -B     要对调的两个 Monitor 值，写 layout.json 里的原始转义文本（脚本列表里打印的就是这种文本，可直接复制）。
#               省略时文件里恰好有两种 Monitor 值才自动对调，否则列出各值与出现次数并报错。
#   -NoRestart  对调后不重新启动 DeskNook。
# 注意：只退出“数据目录与目标 DataDir 相同”的 DeskNook；--exit 走全局事件会关掉所有实例，
#       若还有别的数据目录的实例在运行，脚本会报错而不动文件，请先手动退出它们。
param(
    [string]$DataDir,
    [string]$A,
    [string]$B,
    [switch]$NoRestart
)
$ErrorActionPreference = 'Stop'

# 与 AppPaths.ResolveDataDir 一致：程序在 Program Files 之下用 %AppData%\DeskNook，否则 <exe 目录>\data
function Get-FullPath([string]$Path) { return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/') }
function Test-Under([string]$Path, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { return $false }
    $p = Get-FullPath $Path
    $r = Get-FullPath $Root
    return $p.Equals($r, [StringComparison]::OrdinalIgnoreCase) -or
        $p.StartsWith($r + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Get-ProcDataDir([string]$ExePath) {
    $dir = Split-Path -Parent $ExePath
    $pf = $env:ProgramFiles
    $pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ((Test-Under $dir $pf) -or (Test-Under $dir $pf86)) { return (Join-Path $env:APPDATA 'DeskNook') }
    return (Join-Path $dir 'data')
}

try {
    # 1. 收集运行中的 DeskNook 进程及其数据目录
    $procs = @()
    foreach ($p in @(Get-Process -Name 'DeskNook' -ErrorAction SilentlyContinue)) {
        $exe = $null
        try { $exe = $p.Path } catch { }
        if (-not $exe) { throw "读不到 DeskNook 进程（PID $($p.Id)）的路径，无法确认它用的数据目录。请先手动退出 DeskNook，再用 -DataDir 指定数据目录。" }
        $procs += [pscustomobject]@{ Process = $p; Exe = $exe; Data = (Get-ProcDataDir $exe) }
    }

    # 2. 定位数据目录
    if (-not $DataDir) {
        if ($procs.Count -eq 0) { throw "没有运行中的 DeskNook 进程，无法推导数据目录。请用 -DataDir 指定（含 layout.json 的目录）。" }
        if ($procs.Count -gt 1) { throw "有 $($procs.Count) 个 DeskNook 进程在运行，无法确定数据目录。请用 -DataDir 指定（含 layout.json 的目录）。" }
        $cand = Join-Path (Split-Path -Parent $procs[0].Exe) 'data'
        if (Test-Path -LiteralPath (Join-Path $cand 'layout.json')) { $DataDir = $cand }
        else { $DataDir = Join-Path $env:APPDATA 'DeskNook' }
    }
    $DataDir = Get-FullPath $DataDir
    $layoutPath = Join-Path $DataDir 'layout.json'
    if (-not (Test-Path -LiteralPath $layoutPath)) { throw "找不到 $layoutPath。请用 -DataDir 指定含 layout.json 的数据目录。" }
    Write-Host "数据目录：$DataDir"

    # 3. 先读文件并选定 A/B（出错时还没动任何进程与文件）
    $utf8 = New-Object Text.UTF8Encoding $false
    $text = [IO.File]::ReadAllText($layoutPath, $utf8)
    $pattern = '(?<!\\)"Monitor"\s*:\s*"((?:[^"\\]|\\.)*)"'
    $found = @{}
    foreach ($m in [regex]::Matches($text, $pattern)) {
        $v = $m.Groups[1].Value
        if ($found.ContainsKey($v)) { $found[$v]++ } else { $found[$v] = 1 }
    }
    if ($found.Count -eq 0) { throw "layout.json 里没有任何 Monitor 值，没有可对调的内容。" }
    $listing = ($found.GetEnumerator() | Sort-Object Name | ForEach-Object { "  $($_.Value) 处  $($_.Name)" }) -join "`n"

    if ($A -or $B) {
        if (-not ($A -and $B)) { throw "-A 和 -B 必须同时指定。layout.json 里现有的 Monitor 值：`n$listing" }
        foreach ($v in @($A, $B)) {
            if (-not $found.ContainsKey($v)) { throw "layout.json 里找不到 Monitor 值 $v。现有的值（原始文本）：`n$listing" }
        }
        if ($A -ceq $B) { throw "-A 与 -B 相同，无需对调。" }
    } else {
        if ($found.Count -ne 2) {
            throw "layout.json 里有 $($found.Count) 种不同的 Monitor 值，不是恰好 2 种，无法自动对调。请用 -A/-B 指定要对调的两个（原始文本）：`n$listing"
        }
        $keys = @($found.Keys | Sort-Object)
        $A = $keys[0]; $B = $keys[1]
    }

    # 4. 退出对应的 DeskNook：只处理数据目录相同的进程；--exit 是全局事件，别的实例在跑就拒绝
    $mine = @($procs | Where-Object { (Get-FullPath $_.Data) -ieq $DataDir })
    $others = @($procs | Where-Object { (Get-FullPath $_.Data) -ine $DataDir })
    $restartExe = $null
    if ($mine.Count -gt 0) {
        if ($others.Count -gt 0) {
            throw "另有使用其他数据目录的 DeskNook 在运行（$($others[0].Exe)）。--exit 会关掉所有实例，脚本不会替你关它。请手动退出所有 DeskNook 后重试。"
        }
        $restartExe = $mine[0].Exe
        Write-Host "退出 DeskNook：$restartExe --exit"
        & $restartExe --exit
        $deadline = (Get-Date).AddSeconds(10)
        while (@($mine | Where-Object { -not $_.Process.HasExited }).Count -gt 0) {
            if ((Get-Date) -gt $deadline) { throw "DeskNook 在 10 秒内没有退出，未修改任何文件。请手动退出后重试。" }
            Start-Sleep -Milliseconds 200
        }
    }

    # 5. 备份
    $backup = "$layoutPath.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item -LiteralPath $layoutPath -Destination $backup
    Write-Host "已备份：$backup"

    # 6. 按文本对调（不解码，直接比较原始转义文本）
    $counts = @{ A = 0; B = 0 }
    $swapA = $A; $swapB = $B
    $eval = [Text.RegularExpressions.MatchEvaluator]{
        param($m)
        $g = $m.Groups[1]
        $v = $g.Value
        $head = $m.Value.Substring(0, $g.Index - $m.Index)
        if ($v -ceq $swapA) { $counts.A++; return $head + $swapB + '"' }
        if ($v -ceq $swapB) { $counts.B++; return $head + $swapA + '"' }
        return $m.Value
    }
    $newText = [regex]::Replace($text, $pattern, $eval)

    # 7. 写临时文件再替换，避免留下半写的 layout.json
    $tmp = "$layoutPath.swap-tmp"
    try {
        [IO.File]::WriteAllText($tmp, $newText, $utf8)
        [IO.File]::Replace($tmp, $layoutPath, [NullString]::Value)
    } finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }

    Write-Host "已对调两块显示器："
    Write-Host "  A = $A  （$($counts.A) 处改为 B）"
    Write-Host "  B = $B  （$($counts.B) 处改为 A）"

    # 8. 重启
    if ($restartExe -and -not $NoRestart) {
        Write-Host "重新启动 DeskNook：$restartExe"
        Start-Process -FilePath $restartExe
    } elseif ($restartExe) {
        Write-Host "已按 -NoRestart 跳过重启，需要时请手动启动：$restartExe"
    }
} catch {
    Write-Host "失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
