# 右键菜单 v2 自动化测试（真实桌面）。结果截图/文本输出到 tools/test/out/m-*。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools/test/test-menu-v2.ps1 [-Only 名称片段]
param([string]$Only = '')
. $PSScriptRoot\m-lib.ps1

$Script:ExplorerPids = @()
$Script:Info = @{}
$Script:MapSub = Join-Path $env:TEMP 'xk-test-dir\xk-test-sub'
$Script:TestRoot2 = Join-Path $env:TEMP 'xk-test-dir'

function Want { param([string]$n) return ($Only -eq '' -or $n -like "*$Only*") }
function Assert-ExplorerAlive {
    $now = Get-ExplorerPids
    Assert-True (($now -join ',') -eq ($Script:ExplorerPids -join ',')) "Explorer PID 变了（崩溃？）：之前 $($Script:ExplorerPids -join ',') 现在 $($now -join ',')"
}
function Has-Text { param($Texts, [string]$Pat) return @($Texts | Where-Object { $_ -like "*$Pat*" }).Count -gt 0 }

Backup-AppData
Stop-DeskNook | Out-Null
Clear-TestArtifacts
Clear-NewFolders
if (Test-Path $Script:TestRoot2) { Remove-Item $Script:TestRoot2 -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $Script:MapSub -Force | Out-Null
Set-Content -Path (Join-Path $Script:MapSub 'xk-test-in-sub.txt') -Value 'x'
foreach ($f in 'a', 'b', 'c', 'd', 'e') { New-TestFile -Name "xk-test-$f.txt" -Content "xk-test $f" | Out-Null }
New-TestFile -Name 'xk-test-dir' -Directory | Out-Null
# 先重启 Explorer，确保它加载的是当前构建的扩展 DLL（旧版本 DLL 常驻在旧 Explorer 里会造成版本错位）
Restart-Explorer; Wait-Ms 6000
Wait-Ms 1500
$Script:ExplorerPids = Get-ExplorerPids
Write-Host "Explorer PID：$($Script:ExplorerPids -join ',')"
Minimize-All

try {
    # ---------------------------------------------------------------- 0 导入位置（让原生与 DeskNook 的图标位置一致）
    for ($k = 0; $k -lt 2; $k++) {
        Remove-Item (Join-Path $Script:AppDataDir 'layout.json') -Force -ErrorAction SilentlyContinue
        Start-DeskNook | Out-Null; Wait-Ms 2500; Wait-Saved; Stop-DeskNook | Out-Null; Wait-Ms 2500
    }
    $a = Get-IconCenter 'xk-test-a.txt'
    $bp = Get-BlankPoint

    # ---------------------------------------------------------------- 1 原生菜单 C / C'
    $nItem = $null; $nBg = $null
    if ($true) {
        Invoke-Test 'C 原生图标菜单' {
            $r = Capture-Menu 'm-C-native-item' $a.X $a.Y
            Assert-True ($r.Texts.Count -gt 5) "原生菜单项太少：$($r.Texts -join '|')"
            $Script:Info.NativeItem = $r
            "项数=$($r.Texts.Count)"
        }
        Invoke-Test "C' 原生背景菜单" {
            $r = Capture-Menu 'm-Cp-native-bg' $bp.X $bp.Y
            Assert-True ($r.Texts.Count -gt 5) "原生背景菜单项太少：$($r.Texts -join '|')"
            $Script:Info.NativeBg = $r
            "项数=$($r.Texts.Count)"
        }
        Assert-ExplorerAlive
    }

    # ---------------------------------------------------------------- 2 启动 DeskNook，D / D'
    $shellMark = Get-FileMark $Script:ShellLogPath
    $dnMark = Get-LogMark
    Start-DeskNook | Out-Null; Wait-Ms 3000
    Invoke-Test '代理已加载' {
        $l = Wait-Log -Pattern '菜单代理已(加载|就绪)' -Since $dnMark -TimeoutSec 15
        Assert-True $l '日志里没有「菜单代理已加载/就绪」'
        $Script:Info.LoadLine = $l
        $l
    }
    Invoke-Test 'D 我们的图标菜单（走代理）' {
        $mk = Get-FileMark $Script:ShellLogPath
        $r = Capture-Menu 'm-D-xk-item' $a.X $a.Y
        $line = Wait-FileLog $Script:ShellLogPath '请求 r\d+ kind=item' $mk 5
        Assert-True $line '代理日志里没有 kind=item 的请求（没走代理？）'
        Assert-True ($r.Texts.Count -gt 5) "菜单项太少：$($r.Texts -join '|')"
        $Script:Info.DnItem = $r
        foreach ($need in '整理至新格子', '整理至新文件夹') { Assert-True (Has-Text $r.Texts $need) "缺少自定义项 $need" }
        "项数=$($r.Texts.Count) 出现耗时=$($r.Ms)ms"
    }
    Invoke-Test "D' 我们的背景菜单（走代理）" {
        $mk = Get-FileMark $Script:ShellLogPath
        $r = Capture-Menu 'm-Dp-xk-bg' $bp.X $bp.Y
        $line = Wait-FileLog $Script:ShellLogPath '请求 r\d+ kind=background' $mk 5
        Assert-True $line '代理日志里没有 kind=background 的请求'
        $Script:Info.DnBg = $r
        Assert-True (-not (Has-Text $r.Texts '一键整理')) '外层不应再有 一键整理'
        Assert-True (Has-Text $r.Texts '桌面整理') '缺少 桌面整理 子菜单'
        "项数=$($r.Texts.Count) 出现耗时=$($r.Ms)ms"
    }
    Invoke-Test '对比：图标菜单 C vs D（除自定义项外一致）' {
        $n1 = Normalize-MenuTexts $Script:Info.NativeItem.Texts
        $n2 = Normalize-MenuTexts $Script:Info.DnItem.Texts
        $p = New-SideBySide $Script:Info.NativeItem.Shot $Script:Info.DnItem.Shot 'm-cmp-item.png' ($a.X - 10) 0 520 1440 '原生 C' '我们 D'
        $same = (($n1 -join '|') -eq ($n2 -join '|'))
        Assert-True $same ("菜单项不一致`n原生: $($n1 -join '|')`n我们: $($n2 -join '|')")
        foreach ($need in '发送到手机', '上传到夸克网盘', '上传到百度网盘', '用手机打开', '同步至其它设备') {
            if (Has-Text $Script:Info.NativeItem.Texts $need) { Assert-True (Has-Text $Script:Info.DnItem.Texts $need) "缺少 $need" }
        }
        "一致，拼接图 $p"
    }
    Invoke-Test "对比：背景菜单 C' vs D'（除自定义项外一致）" {
        $n1 = Normalize-MenuTexts $Script:Info.NativeBg.Texts
        $n2 = Normalize-MenuTexts $Script:Info.DnBg.Texts
        $p = New-SideBySide $Script:Info.NativeBg.Shot $Script:Info.DnBg.Shot 'm-cmp-bg.png' ($bp.X - 10) ($bp.Y - 80) 520 760 "原生 C'" "我们 D'"
        $same = (($n1 -join '|') -eq ($n2 -join '|'))
        Assert-True $same ("背景菜单不一致`n原生: $($n1 -join '|')`n我们: $($n2 -join '|')")
        Assert-True (Has-Text $Script:Info.DnBg.Texts 'NVIDIA') '缺少 NVIDIA 控制面板'
        "一致，拼接图 $p"
    }
    Assert-ExplorerAlive

    # ---------------------------------------------------------------- 3 自定义项实际执行
    if (Want '自定义') {
        Invoke-Test '整理至新格子' {
            $before = Get-BoxCount
            $a = Get-IconCenter 'xk-test-a.txt'
            Click-Mouse $a.X $a.Y -Button Right -Delay 700
            Click-MenuPath @('整理至新格子')
            $ok = Wait-Layout { param($l) @($l.Boxes).Count -eq $before + 1 -and @($l.Boxes | Where-Object { @($_.ItemKeys | Where-Object { $_ -like '*xk-test-a.txt' }).Count -gt 0 }).Count -eq 1 } 6
            Save-Screen -Name 'm-3a-newbox' | Out-Null
            Assert-True $ok '布局里没有包含 xk-test-a.txt 的新格子'
            '已创建格子并放入 a'
        }
        Invoke-Test '整理至新文件夹（多选 + 原位重命名）' {
            $b = Get-IconCenter 'xk-test-b.txt'; $c = Get-IconCenter 'xk-test-c.txt'
            $mk = Get-LogMark
            Click-Mouse $b.X $b.Y; Click-Mouse $c.X $c.Y -Ctrl
            Click-Mouse $c.X $c.Y -Button Right -Delay 700
            Click-MenuPath @('整理至新文件夹')
            $dir = $null
            for ($i = 0; $i -lt 20 -and -not $dir; $i++) {
                Wait-Ms 300
                $dir = Get-ChildItem -LiteralPath $Script:DesktopDir -Directory -Force | Where-Object { $_.Name -like '新建文件夹*' } | Select-Object -First 1
            }
            Assert-True $dir '桌面上没有出现新建文件夹'
            $okMove = $false
            for ($i = 0; $i -lt 20 -and -not $okMove; $i++) {
                Wait-Ms 300
                $okMove = (Test-Path (Join-Path $dir.FullName 'xk-test-b.txt')) -and (Test-Path (Join-Path $dir.FullName 'xk-test-c.txt'))
            }
            Assert-True $okMove '文件没有被移进新文件夹'
            $rn = Wait-Log -Pattern '原位重命名框已显示' -Since $mk -TimeoutSec 8
            Wait-Ms 500
            Save-Screen -Name 'm-3b-newfolder-rename' | Out-Null
            Assert-True $rn '新文件夹没有进入重命名'
            Type-Text 'xk-test-newfolder'; Press-Key Enter; Wait-Ms 800
            Assert-True (Test-Path (Join-Path $Script:DesktopDir 'xk-test-newfolder\xk-test-b.txt')) '重命名后的文件夹内容不对'
            '已新建文件夹、移入 b/c 并进入重命名'
        }
        Invoke-Test '作为桌面格子显示' {
            $d = Get-IconCenter 'xk-test-dir'
            Click-Mouse $d.X $d.Y -Button Right -Delay 700
            Click-MenuPath @('作为桌面格子显示')
            $ok = Wait-Layout { param($l) @($l.Boxes | Where-Object { $_.Kind -eq 'Mapped' -and $_.MappedPath -like '*xk-test-dir' }).Count -ge 1 } 6
            Save-Screen -Name 'm-3c-mappedbox' | Out-Null
            Assert-True $ok '没有创建映射格子'
            '已创建映射格子'
        }
        Assert-ExplorerAlive
    }

    # ---------------------------------------------------------------- 4 背景菜单：桌面整理 子菜单、一键整理、撤销整理
    if (Want '背景') {
        Invoke-Test '背景菜单 桌面整理 子菜单' {
            Click-Mouse $bp.X $bp.Y -Button Right -Delay 900
            Click-MenuPath @('桌面整理') -SettleMs 900
            $texts = @(Get-MenuTexts)
            Save-Screen -Name 'm-4a-submenu' | Out-Null
            Press-Key Escape; Press-Key Escape; Wait-Ms 300
            foreach ($need in '新建格子', '新建映射格子', '一键整理', '撤销整理', '桌面整理设置', '退出桌面整理') { Assert-True (Has-Text $texts $need) "子菜单缺少 $need（$($texts -join '|')）" }
            "子菜单项：$($texts -join '|')"
        }
        Invoke-Test '一键整理 + 撤销整理' {
            $before = Get-BoxCount
            Click-Mouse $bp.X $bp.Y -Button Right -Delay 900
            Click-MenuPath @('桌面整理', '一键整理')
            $ok = Wait-Layout { param($l) @($l.Boxes).Count -gt $before } 6
            Save-Screen -Name 'm-4b-organized' | Out-Null
            Assert-True $ok '一键整理后没有新增格子'
            $after = Get-BoxCount
            Click-Mouse $bp.X $bp.Y -Button Right -Delay 900
            Click-MenuPath @('桌面整理', '撤销整理')
            $ok2 = Wait-Layout { param($l) @($l.Boxes).Count -eq $before } 6
            Save-Screen -Name 'm-4c-undone' | Out-Null
            Assert-True $ok2 "撤销整理后格子数应为 $before"
            "格子 $before → $after → $before"
        }
        Assert-ExplorerAlive
    }

    # ---------------------------------------------------------------- 5 普通资源管理器窗口里的自定义项
    if (Want '资源管理器') {
        Invoke-Test '资源管理器窗口里对文件夹右键出现自定义项并可用' {
            $before = @((Read-Layout).Boxes | Where-Object { $_.Kind -eq 'Mapped' -and $_.MappedPath -like '*xk-test-sub' }).Count
            Start-Process explorer.exe "/select,`"$($Script:MapSub)`""
            Wait-Ms 3500
            Press-Key Apps -Delay 800
            $texts = @(); $dl = (Get-Date).AddSeconds(5)
            do { Wait-Ms 300; $texts = @(Get-MenuTexts) } while (-not (Has-Text $texts '作为桌面格子显示') -and (Get-Date) -lt $dl)
            Wait-Ms 500
            Save-Screen -Name 'm-5a-explorer-menu' | Out-Null
            Assert-True (Has-Text $texts '作为桌面格子显示') "没有「作为桌面格子显示」（$($texts -join '|')）"
            Assert-True (Has-Text $texts '整理至新文件夹') '没有「整理至新文件夹」'
            Assert-True (-not (Has-Text $texts '整理至新格子')) '非桌面项不应出现「整理至新格子」'
            Assert-True (-not (Has-Text $texts '打开所在位置')) '资源管理器里不应出现「打开所在位置」'
            Click-MenuPath @('作为桌面格子显示')
            $ok = Wait-Layout { param($l) @($l.Boxes | Where-Object { $_.Kind -eq 'Mapped' -and $_.MappedPath -like '*xk-test-sub' }).Count -eq $before + 1 } 6
            Assert-True $ok '资源管理器里触发后没有创建映射格子'
            Close-ForegroundWindowAltF4
            '已在资源管理器里创建映射格子'
        }
        Assert-ExplorerAlive
    }

    # ---------------------------------------------------------------- 6 拦截：重命名 / 查看 / 显示桌面图标
    if (Want '拦截') {
        Invoke-Test '菜单「重命名」 → 原位重命名框' {
            $d = Get-IconCenter 'xk-test-d.txt'
            $mk = Get-LogMark
            Click-Mouse $d.X $d.Y -Button Right -Delay 900
            Click-MenuPath @('重命名')
            $l = Wait-Log -Pattern '原位重命名框已显示' -Since $mk -TimeoutSec 8
            Wait-Ms 500
            Save-Screen -Name 'm-6a-rename' | Out-Null
            Press-Key Escape; Wait-Ms 400
            Assert-True $l '没有出现原位重命名框'
            $l
        }
        Invoke-Test '查看 ▸ 小图标/原大小 → DeskNook 图标大小随之变化' {
            Wait-Saved
            $orig = [int](Read-Layout).View.IconSize
            $Script:Info.OrigSize = $orig
            Click-Mouse $bp.X $bp.Y -Button Right -Delay 900
            Click-MenuPath @('查看', '小图标')
            $ok = Wait-Layout { param($l) [int]$l.View.IconSize -eq 32 } 6
            Save-Screen -Name 'm-6b-small' | Out-Null
            $restoreName = if ($orig -ge 80) { '大图标' } elseif ($orig -ge 40) { '中等图标' } else { '小图标' }
            Click-Mouse $bp.X $bp.Y -Button Right -Delay 900
            Click-MenuPath @('查看', $restoreName)
            $ok2 = Wait-Layout { param($l) [int]$l.View.IconSize -eq $orig } 6
            Save-Screen -Name 'm-6b-restored' | Out-Null
            Assert-True $ok '选小图标后 IconSize 没变成 32'
            Assert-True $ok2 "没能恢复到原图标大小 $orig"
            "原=$orig → 32 → $orig"
        }
        Invoke-Test '查看 ▸ 显示桌面图标 → DeskNook 图标隐藏/显示，系统 ListView 保持隐藏' {
            $mk = Get-LogMark
            Assert-True (-not [DnTest.Ext]::SystemListViewVisible()) '切换前系统 ListView 应已隐藏'
            Click-Mouse $bp.X $bp.Y -Button Right -Delay 900
            Click-MenuPath @('查看', '显示桌面图标')
            $l1 = Wait-Log -Pattern 'DeskNook 图标显示状态：隐藏' -Since $mk -TimeoutSec 6
            Wait-Ms 600
            Save-Screen -Name 'm-6c-hidden' | Out-Null
            $sysHidden = -not [DnTest.Ext]::SystemListViewVisible()
            Click-Mouse $bp.X $bp.Y -Button Right -Delay 900
            Save-Screen -Name 'm-6c-hidden-menu' | Out-Null
            Click-MenuPath @('查看', '显示桌面图标')
            $l2 = Wait-Log -Pattern 'DeskNook 图标显示状态：显示' -Since $mk -TimeoutSec 6
            Wait-Ms 600
            Save-Screen -Name 'm-6c-shown' | Out-Null
            $sysHidden2 = -not [DnTest.Ext]::SystemListViewVisible()
            Assert-True $l1 '没有隐藏 DeskNook 图标'
            Assert-True $sysHidden '隐藏后系统 ListView 变可见了'
            Assert-True $l2 '没有恢复显示 DeskNook 图标'
            Assert-True $sysHidden2 '恢复后系统 ListView 变可见了'
            '隐藏/显示均正常，系统 ListView 始终隐藏'
        }
        Assert-ExplorerAlive
    }

    # ---------------------------------------------------------------- 7 格子里的菜单
    if (Want '格子') {
        Invoke-Test '格子内图标右键为原生菜单' {
            $box = Get-Boxes | Where-Object { $_.Kind -eq 'Normal' -and @($_.ItemKeys).Count -gt 0 } | Select-Object -First 1
            Assert-True $box '没有带图标的普通格子'
            $p = Get-BoxIconCenter $box 0
            $mk = Get-FileMark $Script:ShellLogPath
            $r = Capture-Menu 'm-7a-box-item' $p.X $p.Y
            $line = Wait-FileLog $Script:ShellLogPath '请求 r\d+ kind=item' $mk 5
            Assert-True $line '格子内图标右键没走代理'
            Assert-True (Has-Text $r.Texts '打开') "菜单不像原生图标菜单：$($r.Texts -join '|')"
            Assert-True (Has-Text $r.Texts '发送到') '缺少 发送到'
            Assert-True (Has-Text $r.Texts '移出格子') '格子内图标应有 移出格子'
            "项数=$($r.Texts.Count)"
        }
        Invoke-Test '映射格子空白处：目录原生背景菜单 + 格子项' {
            $box = Get-Boxes | Where-Object { $_.Kind -eq 'Mapped' -and $_.MappedPath -like '*xk-test-sub' } | Select-Object -First 1
            if (-not $box) { $box = Get-Boxes | Where-Object { $_.Kind -eq 'Mapped' } | Select-Object -First 1 }
            Assert-True $box '没有映射格子'
            $x = [int]($box.Rect.X + $box.Rect.W - 12); $y = [int]($box.Rect.Y + $box.Rect.H - 12)
            $mk = Get-FileMark $Script:ShellLogPath
            $r = Capture-Menu 'm-7b-mapped-bg' $x $y
            $line = Wait-FileLog $Script:ShellLogPath '请求 r\d+ kind=background folder=(?!::desktop)' $mk 5
            Assert-True $line '映射格子空白处没走代理（目录背景）'
            Assert-True (Has-Text $r.Texts '新建') "缺少原生「新建」：$($r.Texts -join '|')"
            Assert-True (Has-Text $r.Texts '解散格子') '缺少格子项 解散格子'
            Assert-True (Has-Text $r.Texts '折叠') '缺少格子项 折叠'
            "项：$($r.Texts -join '|')"
        }
        Assert-ExplorerAlive
    }

    # ---------------------------------------------------------------- 8 稳定性：连续 30 次弹出/关闭
    if (Want '稳定') {
        Invoke-Test '连续 30 次弹出/关闭（Esc）' {
            $slow = 0; $max = 0; $mk = Get-LogMark
            for ($i = 1; $i -le 30; $i++) {
                $t0 = Get-Date
                if ($i % 2 -eq 0) { Click-Mouse $bp.X $bp.Y -Button Right -Delay 50 } else { $aa = Get-IconCenter 'xk-test-e.txt'; Click-Mouse $aa.X $aa.Y -Button Right -Delay 50 }
                $m = $null
                $deadline = (Get-Date).AddSeconds(2)
                do { $m = Find-MenuItem '属性' -TimeoutMs 100; if (-not $m) { $m = Find-MenuItem '刷新' -TimeoutMs 100 } } while (-not $m -and (Get-Date) -lt $deadline)
                $ms = [int]((Get-Date) - $t0).TotalMilliseconds
                if ($ms -gt $max) { $max = $ms }
                if (-not $m -or $ms -gt 1000) { $slow++ }
                Press-Key Escape -Delay 150; Press-Key Escape -Delay 150
                Assert-ExplorerAlive
            }
            Save-Screen -Name 'm-8-after-loop' | Out-Null
            Assert-True ($slow -eq 0) "有 $slow 次菜单超过 1s 或没出现"
            $logs = @(Get-LogSince $mk)
            Assert-True (@($logs | Where-Object { $_ -match '代理 1s 内无响应' }).Count -eq 0) '出现了代理无响应回退'
            "30 次均 ≤1s，最大 ${max}ms，Explorer PID 不变"
        }
    }

    # ---------------------------------------------------------------- 9 Explorer 重启后代理自动重新加载
    if (Want 'Explorer重启') {
        Invoke-Test 'Explorer 重启后代理自动重载，菜单仍走代理' {
            $mk = Get-LogMark; $smk = Get-FileMark $Script:ShellLogPath
            Restart-Explorer
            Wait-Ms 6000; Minimize-All
            $l = Wait-Log -Pattern '菜单代理已(加载|就绪)（Explorer 重启后重挂）' -Since $mk -TimeoutSec 45
            Assert-True $l '重启后没有重新加载代理'
            Wait-Ms 4000
            $Script:ExplorerPids = Get-ExplorerPids
            $mk2 = Get-FileMark $Script:ShellLogPath
            $r = Capture-Menu 'm-9-after-restart' $bp.X $bp.Y
            $line = Wait-FileLog $Script:ShellLogPath '请求 r\d+ kind=background' $mk2 6
            Assert-True $line '重启后菜单没走代理'
            Assert-True (Has-Text $r.Texts '刷新') '重启后菜单不正常'
            $l
        }
    }

    # ---------------------------------------------------------------- 10 回退：--no-proxy 走进程内菜单
    if (Want '回退') {
        Invoke-Test '--no-proxy 回退到进程内菜单' {
            Stop-DeskNook | Out-Null; Wait-Ms 1500
            $mk = Get-LogMark; $smk = Get-FileMark $Script:ShellLogPath
            Start-DeskNook -AppArgs @('--no-proxy') | Out-Null; Wait-Ms 2500
            $r = Capture-Menu 'm-10-fallback-bg' $bp.X $bp.Y
            Assert-True (Has-Text $r.Texts '刷新') "回退菜单不正常：$($r.Texts -join '|')"
            Assert-True (Has-Text $r.Texts '桌面整理') '回退菜单缺少自定义项'
            $line = Wait-Log -Pattern '菜单类键' -Since $mk -TimeoutSec 5
            Assert-True $line '日志里没有进程内菜单的痕迹'
            $proxied = @(Get-FileSince $Script:ShellLogPath $smk | Where-Object { $_ -match '请求 r\d+ kind=' })
            Assert-True ($proxied.Count -eq 0) '--no-proxy 下不应走代理'
            "回退菜单项数=$($r.Texts.Count)"
        }
    }
}
finally {
    try { Press-Key Escape } catch { }
    Stop-DeskNook | Out-Null
    Wait-Ms 800
    Clear-TestArtifacts
    Clear-NewFolders
    if (Test-Path $Script:TestRoot2) { Remove-Item $Script:TestRoot2 -Recurse -Force -ErrorAction SilentlyContinue }
    Restore-AppData
    try { Restore-All } catch { }
    Get-Process explorer -ErrorAction SilentlyContinue | Out-Null
}
$fails = Show-Summary
Write-Host "Explorer PID 最终：$((Get-ExplorerPids) -join ',')（起始 $($Script:ExplorerPids -join ',')）"
exit $fails
