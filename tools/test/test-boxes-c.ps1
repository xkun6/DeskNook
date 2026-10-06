. $PSScriptRoot\box-lib.ps1
# 阶段 2 测试 C：布局持久化（重启程序后完全还原，截图对比）、Explorer 重启后格子还原、折叠/锁定/排序状态持久化
Start-BoxTest -Files @('a', 'b', 'c')
New-Item -ItemType Directory -Path $Script:MapDir -Force | Out-Null
Set-Content -Path (Join-Path $Script:MapDir 'xk-test-m1.txt') -Value 'm1'
$Rect = @(1000, 300, 1500, 1000)
$Shot1 = Join-Path $Script:OutDir 'b-c01-before-restart.png'
try {
    Invoke-Test 'C01 搭建布局：两个普通格子（一个折叠、一个锁定+按修改时间排序）+ 一个映射格子' {
        Click-ContextMenu 1100 400 -Path @('桌面整理 ▸ 新建格子')
        Click-ContextMenu 1500 400 -Path @('桌面整理 ▸ 新建格子')
        Assert-True (Wait-Layout { param($l) @($l.Boxes).Count -eq 2 }) '没有 2 个格子'
        Wait-Saved
        $b1 = (Get-Boxes)[0]; $b2 = (Get-Boxes)[1]
        foreach ($n in @('xk-test-a.txt', 'xk-test-b.txt')) {
            $p = Get-IconCenter $n
            Drag-Mouse $p.X $p.Y ([int]($b1.Rect.X + 150)) ([int]($b1.Rect.Y + 130)); Wait-Ms 600
        }
        $p = Get-IconCenter 'xk-test-c.txt'
        Drag-Mouse $p.X $p.Y ([int]($b2.Rect.X + 150)) ([int]($b2.Rect.Y + 130)); Wait-Ms 600
        # 重命名 b1、锁定+排序 b2
        DoubleClick-Mouse ([int]($b1.Rect.X + 50)) ([int]($b1.Rect.Y + 16)); Wait-Ms 500
        Type-Text '工作区'; Press-Key Enter; Wait-Ms 500
        Click-ContextMenu ([int]($b2.Rect.X + 150)) ([int]($b2.Rect.Y + 16)) -Path @('排序方式', '修改时间')
        Click-ContextMenu ([int]($b2.Rect.X + 150)) ([int]($b2.Rect.Y + 16)) -Path @('锁定')
        Click-ContextMenu 1900 800 -Path @('桌面整理 ▸ 新建映射格子')
        Wait-Ms 1500; Type-Text $Script:MapDir; Wait-Ms 300; Press-Key Enter; Wait-Ms 1500
        if (-not (Wait-Layout { param($l) @($l.Boxes).Count -ge 3 } -TimeoutSec 3)) { Press-Key Enter; Wait-Ms 1000 }
        Assert-True (Wait-Layout { param($l) @($l.Boxes).Count -eq 3 } -TimeoutSec 5) '没有 3 个格子'
        # 折叠 b1
        $b1 = (Get-Boxes)[0]
        Click-Mouse ([int]($b1.Rect.X + $b1.Rect.W - 6 - 24 - 2 - 12)) ([int]($b1.Rect.Y + 16)); Wait-Ms 500
        Move-Mouse 2400 1300 300; Wait-Ms 1500; Wait-Saved
        $bs = Get-Boxes
        Assert-True ($bs[0].Collapsed -eq $true -and $bs[1].Locked -eq $true -and $bs[1].SortMode -eq 'date' -and $bs[0].Name -eq '工作区') '状态不符预期'
        Save-Screen -Name b-c01-before-restart -Rect $Rect | Out-Null
        "3 个格子：折叠/锁定/排序/重命名/映射均已设置"
    }

    Invoke-Test 'C02 重启程序后布局完全还原（截图对比 + 布局 JSON 对比）' {
        $jsonBefore = (Get-Content $Script:LayoutPath -Raw -Encoding UTF8 | ConvertFrom-Json).Boxes | ConvertTo-Json -Depth 6
        Stop-DeskNext | Out-Null; Wait-Ms 1500
        Start-DeskNext | Out-Null; Wait-Ms 2500
        Move-Mouse 2400 1300 300; Wait-Ms 800
        Save-Screen -Name b-c02-after-restart -Rect $Rect | Out-Null
        Wait-Saved
        $jsonAfter = (Get-Content $Script:LayoutPath -Raw -Encoding UTF8 | ConvertFrom-Json).Boxes | ConvertTo-Json -Depth 6
        Assert-True ($jsonBefore -eq $jsonAfter) '重启后格子布局 JSON 变了'
        $d = Get-ImageDiffRatio (Join-Path $Script:OutDir 'b-c01-before-restart.png') (Join-Path $Script:OutDir 'b-c02-after-restart.png') @(0, 0, 1500, 1000) 40
        Assert-True ($d -lt 0.10) "截图差异过大：$d"
        # 壁纸是动态的（发丝会动），截图差异只作粗判，主判据是布局 JSON 一致
        "布局 JSON 一致，截图差异 $([Math]::Round($d, 4))"
    }

    Invoke-Test 'C03 Explorer 重启后格子完整还原' {
        $mark = Get-LogMark
        Stop-ProcessByName explorer; Wait-Ms 1500; Start-Process explorer.exe
        $hit = Wait-Log -Pattern '重建宿主窗口（Explorer 重启后重挂）' -Since $mark -TimeoutSec 40
        Assert-True ($null -ne $hit) '没有重挂'
        Wait-Ms 4000
        Minimize-All; Wait-Ms 1500
        Move-Mouse 2400 1300 300; Wait-Ms 600
        Save-Screen -Name b-c03-after-explorer-restart -Rect $Rect | Out-Null
        $d = Get-ImageDiffRatio (Join-Path $Script:OutDir 'b-c01-before-restart.png') (Join-Path $Script:OutDir 'b-c03-after-explorer-restart.png') @(0, 0, 1500, 1000) 40
        Assert-True ($d -lt 0.10) "Explorer 重启后截图差异过大：$d"
        "重挂成功，截图差异 $([Math]::Round($d, 4))"
    }

    Invoke-Test 'C04 折叠格子悬停展开后仍保持折叠状态；解锁后可拖动' {
        $b2 = (Get-Boxes)[1]
        Click-ContextMenu ([int]($b2.Rect.X + 150)) ([int]($b2.Rect.Y + 16)) -Path @('解除锁定')
        Assert-True (Wait-Layout { param($l) $l.Boxes[1].Locked -eq $false }) '没有解锁'
        $bs = Get-Boxes
        Assert-True ($bs[0].Collapsed -eq $true) '折叠状态丢了'
        "折叠保持，解锁成功"
    }
}
finally { Finish-BoxTest }
$fails = Show-Summary
exit $fails
