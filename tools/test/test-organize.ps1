. $PSScriptRoot\box-lib.ps1
# 阶段 3 测试：一键整理 / 撤销整理 / 重启后撤销 / 设置窗口改规则
# 注意：一键整理作用于真实桌面的全部自由图标（只改布局不动文件）；开始前备份 layout/settings/organize-undo 三个 json，结束逐字节恢复。
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

$AppDir = $Script:DataDir   # 程序目录下的 data
$Files = @('layout.json', 'settings.json', 'organize-undo.json')
$BakDir = Join-Path $env:TEMP 'xk-stage3-backup'
$Marker = Join-Path $BakDir '_backed'

function Backup-All {
    if (Test-Path $Marker) { return }   # 已有备份（上次中途失败）不覆盖
    New-Item -ItemType Directory -Path $BakDir -Force | Out-Null
    foreach ($f in $Files) {
        $src = Join-Path $AppDir $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $BakDir $f) -Force }
        else { Set-Content -Path (Join-Path $BakDir ($f + '.none')) -Value 'none' }
    }
    Set-Content -Path $Marker -Value 'ok'
}
function Restore-AllJson {
    if (-not (Test-Path $Marker)) { return }
    foreach ($f in $Files) {
        $dst = Join-Path $AppDir $f
        $bak = Join-Path $BakDir $f
        if (Test-Path $bak) { Copy-Item $bak $dst -Force } else { Remove-Item $dst -Force -ErrorAction SilentlyContinue }
        Remove-Item ($dst + '.tmp') -Force -ErrorAction SilentlyContinue
    }
}
function Test-JsonRestored {
    foreach ($f in $Files) {
        $dst = Join-Path $AppDir $f; $bak = Join-Path $BakDir $f
        if (Test-Path $bak) {
            if (-not (Test-Path $dst)) { return "缺少 $f" }
            if (-not [System.Linq.Enumerable]::SequenceEqual([System.IO.File]::ReadAllBytes($dst), [System.IO.File]::ReadAllBytes($bak))) { return "$f 与备份不一致" }
        } elseif (Test-Path $dst) { return "$f 不应存在" }
    }
    return $null
}

$Screen = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$GridCols = [int][Math]::Floor($Screen.Width / 75)
$GridRows = [int][Math]::Floor($Screen.Height / 100)

# 在布局里找一个既没有自由图标也不在格子里的格子，返回其中心点
function Find-BlankCell {
    $l = Read-Layout
    $blocked = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($p in $l.FreeIcons.PSObject.Properties) { [void]$blocked.Add("$($p.Value.Col),$($p.Value.Row)") }
    foreach ($b in @($l.Boxes)) {
        $c0 = [Math]::Floor($b.Rect.X / 75); $c1 = [Math]::Ceiling(($b.Rect.X + $b.Rect.W) / 75) - 1
        $r0 = [Math]::Floor($b.Rect.Y / 100); $r1 = [Math]::Ceiling(($b.Rect.Y + $b.Rect.H) / 100) - 1
        for ($c = $c0; $c -le $c1; $c++) { for ($r = $r0; $r -le $r1; $r++) { [void]$blocked.Add("$c,$r") } }
    }
    for ($c = $GridCols - 1; $c -ge 0; $c--) {
        for ($r = $GridRows - 1; $r -ge 0; $r--) {
            if (-not $blocked.Contains("$c,$r")) { return @{ X = [int]($c * 75 + 37); Y = [int]($r * 100 + 50) } }
        }
    }
    throw '桌面没有空白格子'
}

function Right-ClickBlank { param([string[]]$Path)
    for ($try = 0; $try -lt 2; $try++) {
        try { $p = Find-BlankCell; Click-ContextMenu $p.X $p.Y -Path $Path -SettleMs (900 + 600 * $try); return }
        catch { if ($try -eq 1) { throw }; Wait-Ms 800 }
    }
}

function Get-BoxOfKey { param([string]$Name)
    $l = Read-Layout
    foreach ($b in @($l.Boxes)) { if (@($b.ItemKeys) | Where-Object { $_ -like "*\$Name" }) { return $b } }
    return $null
}
function Test-Free { param([string]$Name)
    $l = Read-Layout
    foreach ($p in $l.FreeIcons.PSObject.Properties) { if ($p.Name -like "*\$Name") { return $true } }
    return $false
}

function Find-Ui { param($Root, [string]$Id)
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function Get-SettingsWindow {
    $deadline = (Get-Date).AddSeconds(5)
    do {
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '桌面整理设置')
        $w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return $null
}
function Click-Ui { param($El)
    $r = $El.Current.BoundingRectangle
    Click-Mouse ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
}

$Names = @('xk-test-a.txt', 'xk-test-b.docx', 'xk-test-c.png', 'xk-test-d.mp4', 'xk-test-e.mp3', 'xk-test-f.zip', 'xk-test-g.lnk', 'xk-test-noext')
$DirName = 'xk-test-dir'
$PreShot = Join-Path $Script:OutDir 'c-00-pre.png'

Stop-DeskNook | Out-Null
Backup-All
Remove-Item (Join-Path $AppDir 'settings.json'), (Join-Path $AppDir 'organize-undo.json') -Force -ErrorAction SilentlyContinue
Clear-TestArtifacts
Minimize-All
Save-Screen -Name c-00-pre | Out-Null    # 测试前的桌面（系统图标）
foreach ($n in $Names) { New-TestFile -Name $n -Content 'xk-test' | Out-Null }
New-TestFile -Name $DirName -Directory | Out-Null
Wait-Ms 1000

try {
    Start-DeskNook | Out-Null; Wait-Ms 1800; Wait-Saved
    $boxCount0 = @((Read-Layout).Boxes).Count
    Save-Screen -Name c-01-before | Out-Null

    Invoke-Test 'C01 整理前：撤销整理置灰、一键整理可用' {
        $p = Find-BlankCell
        Click-Mouse $p.X $p.Y -Button Right; Wait-Ms 700
        $o = Find-MenuItem '一键整理' -Exact; $x = Find-MenuItem '桌面整理' -Exact; Assert-True ($null -ne $x) '菜单里没有 桌面整理 子菜单'; Click-Mouse $x.X $x.Y -Delay 600; $u = Find-MenuItem '撤销整理'
        Save-Screen -Name c-01-menu-initial | Out-Null
        Press-Key Escape; Press-Key Escape
        Assert-True ($o -and $u) '菜单里没有一键整理/撤销整理'
        Assert-True (($u.State -band 3) -ne 0) "撤销整理应置灰，State=$($u.State)"
        Assert-True (($o.State -band 3) -eq 0) "一键整理应可用，State=$($o.State)"
        "一键整理 State=$($o.State)，撤销整理 State=$($u.State)（置灰）"
    }

    Invoke-Test 'C02 一键整理：分类正确、新格子靠右不重叠、虚拟项不动' {
        Right-ClickBlank @('一键整理'); Wait-Ms 1500; Wait-Saved
        Move-Mouse ($Screen.Width - 5) ($Screen.Height - 5) 300
        Save-Screen -Name c-02-organized | Out-Null
        $expect = @{ 'xk-test-a.txt' = '文档'; 'xk-test-b.docx' = '文档'; 'xk-test-c.png' = '图片'; 'xk-test-d.mp4' = '视频';
                     'xk-test-e.mp3' = '音频'; 'xk-test-f.zip' = '压缩包'; 'xk-test-g.lnk' = '快捷方式'; 'xk-test-noext' = '其他'; $DirName = '文件夹' }
        foreach ($k in $expect.Keys) {
            $b = Get-BoxOfKey $k
            Assert-True ($b -ne $null) "$k 没进格子"
            Assert-True ($b.Name -eq $expect[$k]) "$k 应在 $($expect[$k])，实际 $($b.Name)"
            Assert-True (Test-Path (Key-Path $k)) "文件被动了：$k"
        }
        $l = Read-Layout
        $virt = @($l.FreeIcons.PSObject.Properties | Where-Object { $_.Name -like '::*' })
        $inBoxVirt = @(@($l.Boxes) | ForEach-Object { @($_.ItemKeys) } | Where-Object { $_ -like '::*' })
        Assert-True ($inBoxVirt.Count -eq 0) "虚拟项进了格子：$($inBoxVirt -join ',')"
        # 新格子：位于右侧、互不重叠、与剩余自由图标不重叠
        $boxes = @($l.Boxes)
        Assert-True ($boxes.Count -gt $boxCount0) '没有新建格子'
        $cells = @{}
        foreach ($b in $boxes) {
            $c0 = [Math]::Floor($b.Rect.X / 75); $c1 = [Math]::Ceiling(($b.Rect.X + $b.Rect.W) / 75) - 1
            $r0 = [Math]::Floor($b.Rect.Y / 100); $r1 = [Math]::Ceiling(($b.Rect.Y + $b.Rect.H) / 100) - 1
            Assert-True ($b.Rect.X + $b.Rect.W -le $Screen.Width + 1 -and $b.Rect.Y + $b.Rect.H -le $Screen.Height + 1) "格子越界：$($b.Name)"
            for ($c = $c0; $c -le $c1; $c++) { for ($r = $r0; $r -le $r1; $r++) {
                if ($cells.ContainsKey("$c,$r")) { throw "格子重叠：$($b.Name) 与 $($cells["$c,$r"]) 于 ($c,$r)" }
                $cells["$c,$r"] = $b.Name } }
        }
        $newBoxes = @($boxes | Select-Object -Skip $boxCount0)
        $maxRight = ($boxes | ForEach-Object { $_.Rect.X + $_.Rect.W } | Measure-Object -Maximum).Maximum
        Assert-True ($maxRight -ge ($GridCols - 1) * 75 + 74) "没有靠右：最右边缘 $maxRight，网格宽 $($GridCols * 75)"
        "新建 $($newBoxes.Count) 个格子：$(($newBoxes | ForEach-Object { $_.Name }) -join '/')；虚拟/其他自由项 $($virt.Count) 个仍在自由区"
    }

    Invoke-Test 'C03 撤销整理：布局恢复（格子消失、图标回自由区）' {
        Right-ClickBlank @('桌面整理 ▸ 撤销整理'); Wait-Ms 1200; Wait-Saved
        Move-Mouse ($Screen.Width - 5) ($Screen.Height - 5) 300
        Save-Screen -Name c-03-undone | Out-Null
        $l = Read-Layout
        Assert-True (@($l.Boxes).Count -eq $boxCount0) "格子数应为 $boxCount0，实际 $(@($l.Boxes).Count)"
        foreach ($n in ($Names + $DirName)) { Assert-True (Test-Free $n) "$n 没回到自由区" }
        Assert-True (-not (Test-Path (Join-Path $AppDir 'organize-undo.json'))) '撤销后 organize-undo.json 仍存在'
        $d = Get-ImageDiffRatio (Join-Path $Script:OutDir 'c-01-before.png') (Join-Path $Script:OutDir 'c-03-undone.png') @(0, 0, 400, 1380) 40
        Assert-True ($d -lt 0.02) "撤销后画面与整理前差异过大：$d"
        "格子数 $boxCount0，所有测试项回到自由区；与整理前截图差异 $([Math]::Round($d, 4))"
    }

    Invoke-Test 'C04 再整理 → 重启程序 → 撤销整理仍可用' {
        Right-ClickBlank @('一键整理'); Wait-Ms 1500; Wait-Saved
        Assert-True (Test-Path (Join-Path $AppDir 'organize-undo.json')) '没有写出 organize-undo.json'
        Stop-DeskNook | Out-Null; Wait-Ms 1500
        Start-DeskNook | Out-Null; Wait-Ms 2000
        Save-Screen -Name c-04-restarted | Out-Null
        $p = Find-BlankCell
        Click-Mouse $p.X $p.Y -Button Right; Wait-Ms 700
        $x = Find-MenuItem '桌面整理' -Exact; Assert-True ($null -ne $x) '菜单里没有 桌面整理 子菜单'
        Click-Mouse $x.X $x.Y -Delay 600
        $u = Find-MenuItem '撤销整理'
        Save-Screen -Name c-04-menu-after-restart | Out-Null
        Press-Key Escape; Press-Key Escape
        Assert-True ($u -and (($u.State -band 3) -eq 0)) "重启后撤销整理应可用，State=$($u.State)"
        Right-ClickBlank @('桌面整理 ▸ 撤销整理'); Wait-Ms 1200; Wait-Saved
        $l = Read-Layout
        Assert-True (@($l.Boxes).Count -eq $boxCount0) "撤销后格子数应为 $boxCount0，实际 $(@($l.Boxes).Count)"
        foreach ($n in ($Names + $DirName)) { Assert-True (Test-Free $n) "$n 没回到自由区" }
        Save-Screen -Name c-04-undone-after-restart | Out-Null
        '重启后撤销可用且生效'
    }

    Invoke-Test 'C05 设置窗口：打开、改规则（去掉 txt）、保存，整理结果随之变化' {
        Right-ClickBlank @('桌面整理 ▸ 设置')
        $w = Get-SettingsWindow
        Assert-True ($w -ne $null) '设置窗口没打开'
        Wait-Ms 800
        Save-Screen -Name c-05-settings | Out-Null
        Click-Ui (Find-Ui $w 'TabRules'); Wait-Ms 600    # 设置窗口现为“常规 / 整理规则”两页，规则在第二页
        # 选中“文档”（第 3 项），把扩展名去掉 txt
        $items = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
        Assert-True ($items.Count -ge 8) "规则列表项数不对：$($items.Count)"
        Click-Ui $items[2]; Wait-Ms 500
        $ext = Find-Ui $w 'ExtBox'
        $vp = $ext.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $old = $vp.Current.Value
        Assert-True ($old -match '\btxt\b') "文档规则里没有 txt：$old"
        $vp.SetValue(($old -replace '\btxt\b', '').Trim()); Wait-Ms 500
        Save-Screen -Name c-05-settings-edited | Out-Null
        Click-Ui (Find-Ui $w 'BtnSave'); Wait-Ms 800
        Assert-True ((Get-SettingsWindow) -eq $null) '保存后设置窗口没关闭'
        Assert-True (Test-Path (Join-Path $AppDir 'settings.json')) '没有写出 settings.json'
        $json = Get-Content (Join-Path $AppDir 'settings.json') -Raw -Encoding UTF8
        Assert-True ($json -notmatch '"txt"') 'settings.json 里仍有 txt'
        Right-ClickBlank @('一键整理'); Wait-Ms 1500; Wait-Saved
        Move-Mouse ($Screen.Width - 5) ($Screen.Height - 5) 300
        Save-Screen -Name c-05-organized-custom | Out-Null
        $b = Get-BoxOfKey 'xk-test-a.txt'
        Assert-True ($b -ne $null -and $b.Name -eq '其他') "txt 应进“其他”，实际：$($b.Name)"
        $b2 = Get-BoxOfKey 'xk-test-b.docx'
        Assert-True ($b2.Name -eq '文档') 'docx 仍应在文档'
        '改规则后 txt 进“其他”，docx 仍在“文档”'
    }

    Invoke-Test 'C06 设置窗口：恢复默认、上下移动（不保存）' {
        Right-ClickBlank @('桌面整理 ▸ 设置')
        $w = Get-SettingsWindow
        Assert-True ($w -ne $null) '设置窗口没打开'
        Wait-Ms 600
        Click-Ui (Find-Ui $w 'TabRules'); Wait-Ms 600
        Click-Ui (Find-Ui $w 'BtnReset'); Wait-Ms 500
        $items = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
        Click-Ui $items[7]; Wait-Ms 300
        Click-Ui (Find-Ui $w 'BtnUp'); Wait-Ms 500
        Save-Screen -Name c-06-settings-reset-moved | Out-Null
        Click-Ui (Find-Ui $w 'BtnCancel'); Wait-Ms 600
        Assert-True ((Get-SettingsWindow) -eq $null) '取消后设置窗口没关闭'
        $json = Get-Content (Join-Path $AppDir 'settings.json') -Raw -Encoding UTF8
        Assert-True ($json -notmatch '"txt"') '取消不应改写 settings.json'
        '恢复默认/上移可用，取消不落盘'
    }

    # 收尾前撤销最近一次整理（让布局回到整理前，便于肉眼核对）
    try { Right-ClickBlank @('桌面整理 ▸ 撤销整理'); Wait-Ms 1200; Wait-Saved } catch { }
}
finally {
    try { Press-Key Escape } catch { }
    Stop-DeskNook | Out-Null
    Wait-Ms 800
    Clear-TestArtifacts
    Restore-AllJson
    try { Restore-All } catch { }
    Wait-Ms 1200
    Minimize-All
    Save-Screen -Name c-99-restored | Out-Null
    Restore-All
    $err = Test-JsonRestored
    if ($err) { Write-Host "[FAIL] json 恢复：$err" -ForegroundColor Red } else { Write-Host '[PASS] 三个 json 已逐字节恢复' -ForegroundColor Green }
    if (-not $err) { Remove-Item $BakDir -Recurse -Force -ErrorAction SilentlyContinue }
    $d = Get-ImageDiffRatio $PreShot (Join-Path $Script:OutDir 'c-99-restored.png') $null 40
    Write-Host ("[INFO] 测试前后桌面截图差异比例：{0:N4}" -f $d)
    Stop-ProcessByName notepad
}
$fails = Show-Summary
exit $fails
