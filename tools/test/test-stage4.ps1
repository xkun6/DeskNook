# 阶段 4 自动化测试（真实桌面）：双击隐藏、托盘、开机自启、设置常规页、第二实例、菜单图标、Explorer 重启、崩溃标记。
# 输出 tools/test/out/h-*。用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools/test/test-stage4.ps1 [-Only 片段]
param([string]$Only = '')
. $PSScriptRoot\h-lib.ps1

$Script:ExplorerPids = @()
$Script:FlagPath = Join-Path $Script:AppDataDir 'running.flag'
$Script:IconRect = @(0, 0, 420, 1300)
function Want { param([string]$n) return ($Only -eq '' -or $n -like "*$Only*") }
function Assert-ExplorerAlive {
    $now = Get-ExplorerPids
    Assert-True (($now -join ',') -eq ($Script:ExplorerPids -join ',')) "Explorer PID 变了（崩溃？）：之前 $($Script:ExplorerPids -join ',') 现在 $($now -join ',')"
}
function Has-Text { param($Texts, [string]$Pat) return @($Texts | Where-Object { $_ -like "*$Pat*" }).Count -gt 0 }
function Shot { param([string]$Name) return (Save-Screen -Name $Name) }

Backup-HState
Stop-XkDesk | Out-Null
Clear-TestArtifacts
Clear-NewFolders
Remove-Item $Script:FlagPath -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $Script:AppDataDir 'settings.json') -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $Script:RunKeyPath -Name XkDesk -ErrorAction SilentlyContinue
foreach ($f in 'a', 'b', 'c') { New-TestFile -Name "xk-test-$f.txt" -Content "xk-test $f" | Out-Null }
Wait-Ms 800
Minimize-All
$bp = Get-BlankPoint

try {
    # ================================================================ 0 菜单组件版本检测（Explorer 里还是旧 DLL 的真实场景）
    if (Want '版本') {
        Remove-Item (Join-Path $Script:AppDataDir 'layout.json') -Force -ErrorAction SilentlyContinue
        $Script:ExplorerPids = Get-ExplorerPids
        $mk = Get-LogMark
        $shellDlls = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'XkDesk\shellext') -Filter 'XkShellExt.*.dll' -ErrorAction SilentlyContinue | ForEach-Object Name)
        Write-Host "启动前已有的 DLL 副本：$($shellDlls -join ', ')"
        Start-XkDesk | Out-Null
        Invoke-Test '版本检测：旧版组件（Explorer 内）→ 日志警告 + 托盘气泡' {
            $l = Wait-Log -Pattern '菜单组件版本一致|警告：Explorer 内的菜单组件是旧版' -Since $mk -TimeoutSec 20
            Assert-True $l '日志里没有版本检测结果'
            Start-Sleep -Milliseconds 1500
            Shot 'h-00-version-balloon' | Out-Null
            "$l"
        }
        Wait-Ms 4000
        Stop-XkDesk | Out-Null
        Wait-Ms 1500
        Assert-ExplorerAlive
    }

    # ================================================================ 重启 Explorer 以加载最新 DLL（之后用例的基线）
    Restart-Explorer; Wait-Ms 7000
    $Script:ExplorerPids = Get-ExplorerPids
    Write-Host "Explorer PID：$($Script:ExplorerPids -join ',')"
    Minimize-All
    Remove-Item (Join-Path $Script:AppDataDir 'layout.json') -Force -ErrorAction SilentlyContinue
    Start-XkDesk | Out-Null; Wait-Ms 2500; Wait-Saved; Stop-XkDesk | Out-Null; Wait-Ms 2000

    if (Want '版本二') {
        $mk = Get-LogMark
        Start-XkDesk | Out-Null
        Invoke-Test '版本检测：重启 Explorer 后版本一致，不提示' {
            $l = Wait-Log -Pattern '菜单组件版本一致|警告：Explorer 内的菜单组件是旧版' -Since $mk -TimeoutSec 20
            Assert-True ($l -and $l -match '版本一致') "期望版本一致，实际：$l"
            Assert-True (-not (Wait-Log -Pattern '托盘气泡' -Since $mk -TimeoutSec 2)) '版本一致时不应弹气泡'
            $l
        }
        Stop-XkDesk | Out-Null; Wait-Ms 1500
    }

    if (Want '版本三') {
        $mk = Get-LogMark
        Start-XkDesk -AppArgs @('--simulate-outdated-proxy') | Out-Null
        Invoke-Test '版本检测（模拟旧版）：日志警告 + 托盘气泡' {
            $l = Wait-Log -Pattern '警告：Explorer 内的菜单组件是旧版' -Since $mk -TimeoutSec 20
            Assert-True $l '日志里没有旧版警告'
            $b = Wait-Log -Pattern '托盘气泡：xk-desk - 已更新右键菜单组件，重启资源管理器后生效' -Since $mk -TimeoutSec 5
            Assert-True $b '日志里没有托盘气泡记录'
            Wait-Ms 1200
            Shot 'h-00b-balloon' | Out-Null
            Save-Screen -Name 'h-00b-balloon-crop' -Rect @(1860, 1000, 700, 440) | Out-Null
            Wait-Ms 5000
            $l
        }
        Stop-XkDesk | Out-Null; Wait-Ms 1500
    }

    # ================================================================ 1 双击空白处隐藏 / 显示
    $mk = Get-LogMark
    Start-XkDesk | Out-Null; Wait-Ms 2500
    $base = Shot 'h-01-visible'
    Invoke-Test '双击空白处隐藏（淡出）→ 只剩壁纸' {
        DoubleClick-Mouse $bp.X $bp.Y -Delay 40
        Save-Screen -Name 'h-02a-fading' -Rect @(0, 0, 420, 700) | Out-Null
        Wait-Ms 700
        $hid = Shot 'h-02-hidden'
        $d = Rect-Diff $base $hid $Script:IconRect
        Assert-True ($d -gt 0.02) "图标区域没有变化（diff=$d）"
        Assert-True (-not [XkTest.Ext]::SystemListViewVisible()) '隐藏状态下系统 ListView 不应出现'
        Assert-True (Wait-Log -Pattern 'XkDesk 图标显示状态：隐藏' -Since $mk -TimeoutSec 3) '日志没有隐藏记录'
        Wait-Saved
        Assert-True ((Read-Json 'layout.json').View.IconsHidden -eq $true) 'layout.json 没有持久化隐藏状态'
        "diff=$([Math]::Round($d, 3))"
    }
    Invoke-Test '隐藏时右键空白处仍弹背景菜单' {
        $r = Capture-Menu 'h-03-hidden-menu' $bp.X $bp.Y
        Assert-True ($r.Texts.Count -gt 5) "菜单项太少：$($r.Texts -join '|')"
        Assert-True (Has-Text $r.Texts '刷新') '缺少 刷新'
        Assert-True (Has-Text $r.Texts '一键整理') '缺少 一键整理'
        "项数=$($r.Texts.Count)"
    }
    Invoke-Test '重启程序后保持隐藏' {
        Stop-XkDesk | Out-Null; Wait-Ms 1500
        Assert-True ([XkTest.Ext]::SystemListViewVisible()) '退出后系统图标应恢复'
        Start-XkDesk | Out-Null; Wait-Ms 2500
        $hid2 = Shot 'h-04-restart-hidden'
        $d = Rect-Diff $base $hid2 $Script:IconRect
        Assert-True ($d -gt 0.02) "重启后图标又出现了（diff=$d）"
        Assert-True (-not [XkTest.Ext]::SystemListViewVisible()) '系统 ListView 不应出现'
        "diff=$([Math]::Round($d, 3))"
    }
    Invoke-Test '再次双击空白处恢复（淡入）' {
        DoubleClick-Mouse $bp.X $bp.Y -Delay 700
        $vis = Shot 'h-05-shown'
        $d = Rect-Diff $base $vis $Script:IconRect
        Assert-True ($d -lt 0.01) "恢复后与初始不一致（diff=$d）"
        Wait-Saved
        Assert-True ((Read-Json 'layout.json').View.IconsHidden -eq $false) 'layout.json 隐藏状态应为 false'
        "diff=$([Math]::Round($d, 4))"
    }
    Assert-ExplorerAlive

    # ================================================================ 2 设置：第二实例打开设置窗口；关闭双击开关后双击无效
    Invoke-Test '第二次启动 → 设置窗口；关闭双击隐藏后双击无效' {
        $mk2 = Get-LogMark
        Start-Process -FilePath $Script:ExePath | Out-Null   # 第二实例 → 打开设置窗口
        $w = Get-UiaWindow 'xk-desk 设置' 6000
        Assert-True $w '第二次启动后没有出现设置窗口'
        Assert-True (Wait-Log -Pattern '收到第二个实例的请求：打开设置窗口' -Since $mk2 -TimeoutSec 3) '日志没有第二实例请求'
        Wait-Ms 600
        Save-Screen -Name 'h-06-settings-general' -Rect (Get-WindowRect $w) | Out-Null
        Assert-True (@(Get-Process -Name XkDesk).Count -eq 1) '第二个实例应自行退出（只剩一个 XkDesk 进程）'
        $chk = Get-UiaById $w 'DblChk'
        Assert-True ($chk.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') '双击开关默认应为开'
        Click-Uia $chk
        Click-Uia (Get-UiaById $w 'BtnSave') 800
        Wait-Ms 500
        Assert-True ((Read-Json 'settings.json').DoubleClickToggle -eq $false) 'settings.json 没有保存 DoubleClickToggle=false'
        DoubleClick-Mouse $bp.X $bp.Y -Delay 800
        $after = Shot 'h-07-dblclick-disabled'
        $d = Rect-Diff $base $after $Script:IconRect
        Assert-True ($d -lt 0.01) "关闭后双击仍然隐藏了（diff=$d）"
        "diff=$([Math]::Round($d, 4))"
    }
    # 恢复开关，供后续用例使用
    Start-Process -FilePath $Script:ExePath | Out-Null
    $w = Get-UiaWindow 'xk-desk 设置' 6000
    if ($w) { Click-Uia (Get-UiaById $w 'DblChk'); Click-Uia (Get-UiaById $w 'BtnSave') 800 }
    Wait-Ms 500

    # ================================================================ 3 托盘
    if (Want '托盘') {
        Invoke-Test '托盘图标出现 + 右键菜单' {
            $t = Locate-TrayIcon
            Assert-True $t '找不到托盘图标（UIA）'
            Shot 'h-10-tray-area' | Out-Null
            Close-TrayOverflow
            $texts = Open-TrayMenu
            Shot 'h-11-tray-menu' | Out-Null
            foreach ($need in '一键整理', '撤销整理', '新建格子', '隐藏桌面图标', '设置', '开机自启', '退出') { Assert-True (Has-Text $texts $need) "托盘菜单缺少：$need（$($texts -join '|')）" }
            Press-Key Escape; Press-Key Escape
            "位置=($($t.X),$($t.Y)) 在 $($t.Window)；菜单：$($texts -join '|')"
        }
        Invoke-Test '托盘：一键整理 → 撤销整理' {
            Open-TrayMenu | Out-Null; Click-TrayMenuItem '一键整理' 1200
            $ok = Wait-Layout { param($l) @($l.Boxes).Count -gt 0 } 6
            Assert-True $ok '一键整理后没有格子'
            Shot 'h-12-tray-organized' | Out-Null
            Open-TrayMenu | Out-Null; Click-TrayMenuItem '撤销整理' 1200
            $ok = Wait-Layout { param($l) @($l.Boxes).Count -eq 0 } 6
            Assert-True $ok '撤销整理后格子应消失'
            Shot 'h-13-tray-undone' | Out-Null
            '整理 / 撤销 正常'
        }
        Invoke-Test '托盘：新建格子' {
            Open-TrayMenu | Out-Null; Click-TrayMenuItem '新建格子' 1000
            $ok = Wait-Layout { param($l) @($l.Boxes).Count -eq 1 } 6
            Assert-True $ok '托盘新建格子后布局里应有 1 个格子'
            $nb = Get-Boxes | Select-Object -First 1
            Drag-Mouse ([int]($nb.Rect.X + 60)) ([int]($nb.Rect.Y + 14)) 700 150     # 挪到左上，避开屏幕中央（设置窗口 / 空白点）
            Wait-Saved
            Shot 'h-14-tray-newbox' | Out-Null
            '已创建'
        }
        Invoke-Test '托盘：菜单隐藏/显示 + 左键切换' {
            Open-TrayMenu | Out-Null; Click-TrayMenuItem '隐藏桌面图标' 900
            $hid = Shot 'h-15-tray-hidden'
            Assert-True ((Rect-Diff $base $hid $Script:IconRect) -gt 0.02) '菜单隐藏后图标区没变化'
            $texts = Open-TrayMenu
            Assert-True (Has-Text $texts '显示桌面图标') "隐藏后菜单应显示“显示桌面图标”（$($texts -join '|')）"
            Press-Key Escape; Press-Key Escape -Delay 300
            $t = Locate-TrayIcon
            Click-Mouse $t.X $t.Y -Delay 900            # 左键 → 显示
            Close-TrayOverflow
            Shot 'h-16-tray-left-shown' | Out-Null
            Wait-Saved
            Assert-True ((Read-Json 'layout.json').View.IconsHidden -eq $false) '左键单击后应为显示状态'
            $t = Locate-TrayIcon
            Click-Mouse $t.X $t.Y -Delay 900            # 左键 → 隐藏
            $hid2 = Shot 'h-17-tray-left-hidden'
            Assert-True ((Rect-Diff $base $hid2 $Script:IconRect) -gt 0.02) '左键单击后图标区没变化'
            $t = Locate-TrayIcon
            Click-Mouse $t.X $t.Y -Delay 900            # 再显示
            Close-TrayOverflow
            '托盘菜单与左键切换正常'
        }
        Invoke-Test '托盘：开机自启切换（Run 注册表值）' {
            Assert-True ($null -eq (Get-RunValue)) '测试前不应有自启值'
            Open-TrayMenu | Out-Null; Click-TrayMenuItem '开机自启' 800
            $v = Get-RunValue
            Assert-True ($v -eq ('"' + $Script:ExePath + '"')) "自启值不正确：$v"
            Open-TrayMenu | Out-Null
            Shot 'h-18-tray-autostart-checked' | Out-Null
            Click-TrayMenuItem '开机自启' 800
            Assert-True ($null -eq (Get-RunValue)) '关闭后自启值应被删除'
            Close-TrayOverflow
            "值=$v"
        }
    }

    # ================================================================ 4 设置窗口常规页：透明度实时预览、图标大小
    if (Want '设置') {
        Invoke-Test '设置：透明度实时预览 + 图标大小切换 + 自启复选框' {
            $w = Get-UiaWindow 'xk-desk 设置' 1500
            if (-not $w) { Start-Process -FilePath $Script:ExePath | Out-Null; $w = Get-UiaWindow 'xk-desk 设置' 6000 }
            Assert-True $w '设置窗口未出现'
            $box = (Get-Boxes | Select-Object -First 1)
            Assert-True $box '需要一个格子来观察透明度'
            $rect = @([int]$box.Rect.X, [int]$box.Rect.Y + 4, 60, 20)   # 格子标题栏左侧一小块背景
            $s0 = Save-Screen -Name 'h-20-opacity-default' -Rect $rect
            $slider = Get-UiaById $w 'OpacitySlider'
            $rv = $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
            $rv.SetValue(0.25); Wait-Ms 600
            $s1 = Save-Screen -Name 'h-20-opacity-low' -Rect $rect
            Save-Screen -Name 'h-21-settings-opacity' -Rect (Get-WindowRect $w) | Out-Null
            Shot 'h-21-desktop-opacity-low' | Out-Null
            $d = Get-ImageDiffRatio -PathA $s0 -PathB $s1 -Tolerance 6
            Assert-True ($d -gt 0.3) "透明度变化未在格子上实时体现（diff=$d）"
            # 取消 → 还原
            Click-Uia (Get-UiaById $w 'BtnCancel') 900
            $s2 = Save-Screen -Name 'h-22-opacity-after-cancel' -Rect $rect
            $d2 = Get-ImageDiffRatio -PathA $s0 -PathB $s2 -Tolerance 6
            Assert-True ($d2 -lt 0.1) "取消后透明度未还原（diff=$d2）"
            # 图标大小：大 → 保存
            Start-Process -FilePath $Script:ExePath | Out-Null
            $w = Get-UiaWindow 'xk-desk 设置' 6000; Wait-Ms 500
            Click-Uia (Get-UiaById $w 'SizeLarge')
            Click-Uia (Get-UiaById $w 'BtnSave') 1200
            Wait-Saved
            Assert-True ((Read-Json 'layout.json').View.IconSize -eq 96) "图标大小应为 96：$((Read-Json 'layout.json').View.IconSize)"
            Assert-True ((Read-Json 'settings.json').IconSizeMode -eq 'large') 'settings.json 应为 large'
            Shot 'h-23-icons-large' | Out-Null
            # 改回跟随系统
            Start-Process -FilePath $Script:ExePath | Out-Null
            $w = Get-UiaWindow 'xk-desk 设置' 6000; Wait-Ms 500
            Click-Uia (Get-UiaById $w 'SizeSystem')
            Click-Uia (Get-UiaById $w 'BtnSave') 1200
            Wait-Saved
            Assert-True ((Read-Json 'layout.json').View.IconSize -eq 48) "跟随系统后应回到 48：$((Read-Json 'layout.json').View.IconSize)"
            Shot 'h-24-icons-system' | Out-Null
            # 设置窗口里的自启复选框
            Start-Process -FilePath $Script:ExePath | Out-Null
            $w = Get-UiaWindow 'xk-desk 设置' 6000; Wait-Ms 500
            Click-Uia (Get-UiaById $w 'AutoChk') 600
            Assert-True ((Get-RunValue) -eq ('"' + $Script:ExePath + '"')) '设置里勾选自启后注册表值不对'
            Click-Uia (Get-UiaById $w 'AutoChk') 600
            Assert-True ($null -eq (Get-RunValue)) '取消勾选后自启值应被删除'
            Click-Uia (Get-UiaById $w 'BtnCancel') 600
            "透明度 diff=$([Math]::Round($d, 2)) 取消后 diff=$([Math]::Round($d2, 3))"
        }
    }

    # ================================================================ 5 菜单图标 + 打开所在位置显示规则
    if (Want '菜单') {
        Invoke-Test '菜单图标：图标菜单（自由区项）' {
            Press-Key Escape
            $a = Get-IconCenter 'xk-test-a.txt'
            $r = Capture-Menu 'h-30-item-menu' $a.X $a.Y
            Assert-True (Has-Text $r.Texts '整理至新格子') '缺少 整理至新格子'
            Assert-True (-not (Has-Text $r.Texts '打开所在位置')) '桌面自由区的项不应出现“打开所在位置”'
            Save-Screen -Name 'h-30-item-menu-crop' -Rect @($a.X, $a.Y, 460, 760) | Out-Null
            '自由区项：有“整理至新格子”，无“打开所在位置”'
        }
        Invoke-Test '菜单图标：背景菜单 + xk-desk 子菜单' {
            Click-Mouse $bp.X $bp.Y -Button Right -Delay 1200
            $m = Find-MenuItem 'xk-desk' -TimeoutMs 4000
            Assert-True $m '背景菜单缺少 xk-desk'
            Move-Mouse $m.X $m.Y 1200
            Save-Screen -Name 'h-31-bg-submenu' -Rect @(($bp.X - 20), ($bp.Y - 80), 760, 800) | Out-Null
            Shot 'h-31-bg-submenu-full' | Out-Null
            Press-Key Escape; Press-Key Escape -Delay 300
            '已截图'
        }
        Invoke-Test '“打开所在位置”只在格子内的项上出现' {
            # 一键整理后图标都在格子里
            Click-Mouse $bp.X $bp.Y -Button Right -Delay 1200
            Click-MenuPath @('一键整理')
            Wait-Ms 1200; Wait-Saved
            $boxes = Get-Boxes
            $b = $boxes | Where-Object { @($_.ItemKeys | Where-Object { $_ -like '*xk-test-a.txt' }).Count -gt 0 } | Select-Object -First 1
            Assert-True $b '找不到包含 xk-test-a.txt 的格子'
            $key = @($b.ItemKeys) | Where-Object { $_ -like '*xk-test-a.txt' } | Select-Object -First 1
            $idx = [array]::IndexOf(@($b.ItemKeys), $key)
            $c = Get-BoxIconCenter $b $idx
            $r = Capture-Menu 'h-32-boxitem-menu' $c.X $c.Y
            Assert-True (Has-Text $r.Texts '打开所在位置') "格子内的项应出现“打开所在位置”：$($r.Texts -join '|')"
            Save-Screen -Name 'h-32-boxitem-menu-crop' -Rect @($c.X, $c.Y, 460, 800) | Out-Null
            '格子内项：有“打开所在位置”'
        }
    }

    # ================================================================ 6 Explorer 重启：托盘重建 + 代理仍工作
    if (Want 'Explorer') {
        Invoke-Test 'Explorer 重启后托盘图标重建、菜单代理仍工作' {
            Close-SettingsWindow
            $mk3 = Get-LogMark
            Restart-Explorer; Wait-Ms 8000
            $Script:ExplorerPids = Get-ExplorerPids
            $l = Wait-Log -Pattern '托盘图标已添加' -Since $mk3 -TimeoutSec 20
            Assert-True $l '日志里没有 Explorer 重启后的托盘重建'
            Wait-Ms 2500
            $t = Locate-TrayIcon
            Assert-True $t 'Explorer 重启后找不到托盘图标'
            Shot 'h-40-tray-after-restart' | Out-Null
            Close-TrayOverflow
            Minimize-All
            Click-Mouse $bp.X $bp.Y -Button Right -Delay 1500
            $texts = @(Get-MenuTexts)
            Shot 'h-41-menu-after-restart' | Out-Null
            Press-Key Escape; Press-Key Escape
            Assert-True (Has-Text $texts 'xk-desk') "重启后背景菜单缺少 xk-desk（代理失效？）：$($texts -join '|')"
            Assert-True (-not [XkTest.Ext]::SystemListViewVisible()) '重启后系统 ListView 应保持隐藏'
            "菜单项=$($texts.Count)"
        }
    }

    # ================================================================ 7 崩溃标记 / 正常退出
    if (Want '崩溃') {
        Invoke-Test '崩溃兜底：强杀后重启日志记录异常退出；正常退出删除标记' {
            Assert-True (Test-Path $Script:FlagPath) '运行期间应存在 running.flag'
            Stop-ProcessByName XkDesk; Wait-Ms 800
            [void][XkTest.Native]::ShowSystemIcons()
            Assert-True (Test-Path $Script:FlagPath) '强杀后标记应残留'
            $mk4 = Get-LogMark
            Start-XkDesk | Out-Null; Wait-Ms 1500
            Assert-True (Wait-Log -Pattern '检测到上次异常退出' -Since $mk4 -TimeoutSec 5) '日志没有记录上次异常退出'
            Stop-XkDesk | Out-Null; Wait-Ms 800
            Assert-True (-not (Test-Path $Script:FlagPath)) '正常退出后应删除 running.flag'
            Assert-True ([XkTest.Ext]::SystemListViewVisible()) '正常退出后系统图标应恢复'
            '强杀后残留标记 → 重启记录日志 → 正常退出清除'
        }
    }

    # ================================================================ 8 托盘“退出”
    if (Want '退出') {
        Invoke-Test '托盘退出：进程结束、系统图标恢复' {
            Start-XkDesk | Out-Null; Wait-Ms 2500
            Open-TrayMenu | Out-Null; Click-TrayMenuItem '退出' 1500
            $deadline = (Get-Date).AddSeconds(8)
            while ((Get-Date) -lt $deadline -and (Get-Process -Name XkDesk -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
            Assert-True (-not (Get-Process -Name XkDesk -ErrorAction SilentlyContinue)) '托盘退出后进程仍在'
            Assert-True ([XkTest.Ext]::SystemListViewVisible()) '退出后系统图标应恢复'
            Wait-Ms 1000
            $t = Locate-TrayIcon
            Assert-True (-not $t) '退出后托盘图标应消失'
            '已退出'
        }
    }
    Assert-ExplorerAlive
}
finally {
    try { Close-SettingsWindow } catch { }
    try { Press-Key Escape } catch { }
    Stop-XkDesk | Out-Null
    Wait-Ms 800
    [void][XkTest.Native]::ShowSystemIcons()
    Clear-TestArtifacts
    Clear-NewFolders
    Remove-Item $Script:FlagPath -Force -ErrorAction SilentlyContinue
    Restore-HState
    try { Restore-All } catch { }
    Stop-ProcessByName notepad
}
$fails = Show-Summary
Write-Host "XkDesk 进程：$(@(Get-Process -Name XkDesk -ErrorAction SilentlyContinue).Count)；系统图标可见：$([XkTest.Ext]::SystemListViewVisible())；Run 值：$(Get-RunValue)"
exit $fails
