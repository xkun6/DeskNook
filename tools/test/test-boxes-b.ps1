. $PSScriptRoot\box-lib.ps1
# 阶段 2 测试 B：移动到格子/移出格子菜单、格子内原生菜单、用选中项新建格子、映射格子（同步/菜单/拖放）、解散
Start-BoxTest -Files @('a', 'b', 'c', 'd')
$KA = Key-Path 'xk-test-a.txt'; $KB = Key-Path 'xk-test-b.txt'; $KC = Key-Path 'xk-test-c.txt'; $KD = Key-Path 'xk-test-d.txt'
New-Item -ItemType Directory -Path $Script:MapDir -Force | Out-Null
Set-Content -Path (Join-Path $Script:MapDir 'xk-test-m1.txt') -Value 'm1'
Set-Content -Path (Join-Path $Script:MapDir 'xk-test-m2.txt') -Value 'm2'
try {
    Invoke-Test 'B01 新建空格子，用「移动到格子」菜单把图标移入' {
        Click-ContextMenu 1500 300 -Path @('新建格子')
        Assert-True (Wait-Layout { param($l) @($l.Boxes).Count -eq 1 }) '没有新格子'
        Wait-Saved
        $p = Get-IconCenter 'xk-test-a.txt'
        Click-Mouse $p.X $p.Y -Button Right; Wait-Ms 800
        $texts = Get-MenuTexts
        Assert-True ($texts -contains '移动到格子') "菜单里没有「移动到格子」：$($texts -join '|')"
        Assert-True (-not ($texts -contains '移出格子')) '自由区图标不应有「移出格子」'
        $m = Find-MenuItem '移动到格子'; Click-Mouse $m.X $m.Y; Wait-Ms 600
        Save-Screen -Name b-b01-move-submenu -Rect @(([int]($p.X - 100)), ([int]($p.Y - 100)), ([int](700)), ([int](600))) | Out-Null
        $sub = Find-MenuItem '新格子' -Exact; Assert-True ($null -ne $sub) '子菜单里没有格子'
        Click-Mouse $sub.X $sub.Y; Wait-Ms 600; Wait-Saved
        $l = Read-Layout
        Assert-True (@($l.Boxes[0].ItemKeys) -contains $KA) '没有移入格子'
        Assert-True (Test-Path $KA) '文件被动了'
        "a 已移入格子（文件未动）"
    }

    Invoke-Test 'B02 格子内图标右键 = 原生菜单 + 移出格子' {
        $bx = (Get-Boxes)[0]
        $p = Get-BoxIconCenter $bx 0
        Click-Mouse $p.X $p.Y -Button Right; Wait-Ms 900
        Save-Screen -Name b-b02-box-icon-menu -Rect @(([int]($p.X - 50)), ([int]($p.Y - 50)), ([int](700)), ([int](900))) | Out-Null
        $texts = Get-MenuTexts
        foreach ($need in @('打开', '剪切', '复制', '删除', '重命名', '属性', '移出格子')) {
            Assert-True ($texts -contains $need) "原生菜单缺少「$need」：$($texts -join '|')"
        }
        $m = Find-MenuItem '移出格子'; Click-Mouse $m.X $m.Y; Wait-Ms 700; Wait-Saved
        $l = Read-Layout
        Assert-True (@($l.Boxes[0].ItemKeys).Count -eq 0) '没有移出'
        $free = $l.FreeIcons.PSObject.Properties | Where-Object { $_.Name -eq $KA }
        Assert-True ($null -ne $free -and $null -eq $free.Value.LastSeenUtc) '没有回到自由区'
        "原生菜单项齐全（$($texts.Count) 项），移出后回到自由区"
    }

    Invoke-Test 'B03 选中多个桌面图标 → 用选中项新建格子' {
        $pa = Get-IconCenter 'xk-test-a.txt'; $pb = Get-IconCenter 'xk-test-b.txt'
        Click-Mouse $pa.X $pa.Y; Click-Mouse $pb.X $pb.Y -Ctrl
        Click-ContextMenu $pb.X $pb.Y -Path @('用选中项新建格子')
        Assert-True (Wait-Layout { param($l) @($l.Boxes).Count -eq 2 }) '没有新格子'
        Wait-Saved
        $nb = (Get-Boxes)[1]
        Assert-True (@($nb.ItemKeys).Count -eq 2 -and @($nb.ItemKeys) -contains $KA -and @($nb.ItemKeys) -contains $KB) "成员不对：$(@($nb.ItemKeys) -join ',')"
        Move-Mouse 2200 1250 200
        Save-Screen -Name b-b03-from-selection -Rect @(([int](1200)), ([int](200)), ([int](900)), ([int](900))) | Out-Null
        "新格子 $($nb.Name) 含 2 项，位置 $($nb.Rect.X),$($nb.Rect.Y)"
    }

    Invoke-Test 'B04 新建映射格子（IFileOpenDialog 选目录）' {
        Click-ContextMenu 1900 800 -Path @('新建映射格子')
        Wait-Ms 1500
        Save-Screen -Name b-b04-folder-dialog | Out-Null
        Type-Text $Script:MapDir; Wait-Ms 300; Press-Key Enter; Wait-Ms 1500
        Save-Screen -Name b-b04-after-dialog | Out-Null
        if (-not (Wait-Layout { param($l) @($l.Boxes).Count -ge 3 } -TimeoutSec 4)) { Press-Key Enter; Wait-Ms 1000 }
        Assert-True (Wait-Layout { param($l) @($l.Boxes | Where-Object { $_.Kind -eq 'Mapped' }).Count -eq 1 }) '没有映射格子'
        Wait-Ms 1000; Wait-Saved
        $mb = Get-Boxes | Where-Object { $_.Kind -eq 'Mapped' } | Select-Object -First 1
        Assert-True ($mb.MappedPath -ieq $Script:MapDir) "MappedPath 不对：$($mb.MappedPath)"
        Move-Mouse 2200 1250 200
        Save-Screen -Name b-b04-mapped -Rect @(([int](1600)), ([int](500)), ([int](800)), ([int](700))) | Out-Null
        "映射格子 $($mb.Name) → $($mb.MappedPath)，位置 $($mb.Rect.X),$($mb.Rect.Y)"
    }

    Invoke-Test 'B05 映射目录新建/删除文件 1.5 秒内同步' {
        $f = Join-Path $Script:MapDir 'xk-test-m3.txt'
        $mark = Get-LogMark
        Set-Content -Path $f -Value 'm3'
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $hit = Wait-Log -Pattern '映射目录变化：新增 1' -Since $mark -TimeoutSec 3
        $t1 = $sw.ElapsedMilliseconds
        Assert-True ($null -ne $hit -and $t1 -le 1500) "新建同步耗时 ${t1}ms（>1500 或未同步）"
        Wait-Ms 300
        $mb = Get-Boxes | Where-Object { $_.Kind -eq 'Mapped' } | Select-Object -First 1
        Save-Screen -Name b-b05-mapped-added -Rect @(([int]([int]$mb.Rect.X - 20)), ([int]([int]$mb.Rect.Y - 20)), ([int](360)), ([int](300))) | Out-Null
        $mark = Get-LogMark
        Remove-Item $f -Force
        $sw.Restart()
        $hit = Wait-Log -Pattern '映射目录变化：新增 0，删除 1' -Since $mark -TimeoutSec 3
        $t2 = $sw.ElapsedMilliseconds
        Assert-True ($null -ne $hit -and $t2 -le 1500) "删除同步耗时 ${t2}ms（>1500 或未同步）"
        Wait-Ms 300
        Save-Screen -Name b-b05-mapped-removed -Rect @(([int]([int]$mb.Rect.X - 20)), ([int]([int]$mb.Rect.Y - 20)), ([int](360)), ([int](300))) | Out-Null
        "新建 ${t1}ms，删除 ${t2}ms"
    }

    Invoke-Test 'B06 映射格子内图标右键 = 该目录的原生菜单' {
        $mb = Get-Boxes | Where-Object { $_.Kind -eq 'Mapped' } | Select-Object -First 1
        $p = Get-BoxIconCenter $mb 0
        Click-Mouse $p.X $p.Y -Button Right; Wait-Ms 900
        Save-Screen -Name b-b06-mapped-icon-menu -Rect @(([int]($p.X - 50)), ([int]($p.Y - 50)), ([int](700)), ([int](900))) | Out-Null
        $texts = Get-MenuTexts
        foreach ($need in @('打开', '剪切', '复制', '删除', '属性')) { Assert-True ($texts -contains $need) "缺少「$need」：$($texts -join '|')" }
        Assert-True (-not ($texts -contains '移出格子')) '映射格子内的项不应有「移出格子」'
        Press-Key Escape; Wait-Ms 400
        "原生菜单 $($texts.Count) 项"
    }

    Invoke-Test 'B07 映射格子空白处右键 = 自定义格子菜单 + 该目录背景菜单（含「新建」）' {
        $mb = Get-Boxes | Where-Object { $_.Kind -eq 'Mapped' } | Select-Object -First 1
        $x = [int]($mb.Rect.X + 150); $y = [int]($mb.Rect.Y + $mb.Rect.H - 30)
        Click-Mouse $x $y -Button Right; Wait-Ms 1000
        Save-Screen -Name b-b07-mapped-blank-menu -Rect @(([int]($x - 50)), ([int]($y - 100)), ([int](800)), ([int](1100))) | Out-Null
        $texts = Get-MenuTexts
        foreach ($need in @('新建', '解散格子', '折叠', '锁定')) { Assert-True ($texts -contains $need) "缺少「$need」：$($texts -join '|')" }
        # 从「新建」子菜单创建文本文档，应出现在映射目录里并自动进入重命名
        $m = Find-MenuItem '新建' -Exact; Click-Mouse $m.X $m.Y; Wait-Ms 800
        Save-Screen -Name b-b07-new-submenu -Rect @(([int]($x - 50)), ([int]($y - 100)), ([int](1100)), ([int](1100))) | Out-Null
        Press-Key Escape; Press-Key Escape; Wait-Ms 400
        "背景菜单含：新建/解散格子/折叠/锁定"
    }

    Invoke-Test 'B08 把桌面文件拖进映射格子 → 交给目录的 IDropTarget（文件操作）' {
        $mb = Get-Boxes | Where-Object { $_.Kind -eq 'Mapped' } | Select-Object -First 1
        $p = Get-IconCenter 'xk-test-c.txt'
        Drag-Mouse $p.X $p.Y ([int]($mb.Rect.X + 200)) ([int]($mb.Rect.Y + $mb.Rect.H - 40)); Wait-Ms 2500
        Save-Screen -Name b-b08-drop-mapped -Rect @(([int]([int]$mb.Rect.X - 100)), ([int]([int]$mb.Rect.Y - 100)), ([int](600)), ([int](500))) | Out-Null
        $inMap = Test-Path (Join-Path $Script:MapDir 'xk-test-c.txt')
        Assert-True $inMap '文件没有进到映射目录'
        $onDesk = Test-Path $KC
        "c 已进映射目录；桌面上是否仍有 c（跨盘=复制，同盘=移动）：$onDesk"
    }

    Invoke-Test 'B09 从映射格子把图标拖到桌面空白 → 文件操作到桌面（不是布局移动）' {
        $mb = Get-Boxes | Where-Object { $_.Kind -eq 'Mapped' } | Select-Object -First 1
        $p = Get-BoxIconCenter $mb 1   # 映射格子默认按名称排序：m1, m2, c...
        $items = Get-ChildItem $Script:MapDir | Sort-Object Name
        $target = $items[1].Name
        Drag-Mouse $p.X $p.Y 300 1000; Wait-Ms 3000
        Save-Screen -Name b-b09-drag-out -Rect @(([int](0)), ([int](700)), ([int](900)), ([int](700))) | Out-Null
        $dest = Join-Path $Script:DesktopDir $target
        Assert-True (Test-Path $dest) "桌面上没有出现 $target（拖出应是文件操作）"
        "拖出 $target → 桌面文件已出现"
    }

    Invoke-Test 'B10 解散映射格子：格子消失、目录文件原样保留' {
        $before = @(Get-ChildItem $Script:MapDir).Count
        $mb = Get-Boxes | Where-Object { $_.Kind -eq 'Mapped' } | Select-Object -First 1
        Click-ContextMenu ([int]($mb.Rect.X + 150)) ([int]($mb.Rect.Y + 16)) -Path @('解散格子')
        Assert-True (Wait-Layout { param($l) @($l.Boxes | Where-Object { $_.Kind -eq 'Mapped' }).Count -eq 0 }) '映射格子没有消失'
        $after = @(Get-ChildItem $Script:MapDir).Count
        Assert-True ($before -eq $after) "映射目录文件数变了 $before → $after"
        Save-Screen -Name b-b10-mapped-dissolved -Rect @(([int](1200)), ([int](200)), ([int](1200)), ([int](1000))) | Out-Null
        "映射目录文件数 $before 不变"
    }

    Invoke-Test 'B11 解散普通格子：图标回到桌面格子原位置附近' {
        $nb = Get-Boxes | Where-Object { $_.Kind -eq 'Normal' -and @($_.ItemKeys).Count -eq 2 } | Select-Object -First 1
        Assert-True ($null -ne $nb) '找不到含 2 项的普通格子'
        $col0 = [Math]::Floor($nb.Rect.X / 75); $row0 = [Math]::Floor($nb.Rect.Y / 100)
        Click-ContextMenu ([int]($nb.Rect.X + 150)) ([int]($nb.Rect.Y + 16)) -Path @('解散格子')
        Assert-True (Wait-Layout { param($l) @($l.Boxes | Where-Object { $_.Name -eq $nb.Name }).Count -eq 0 }) '格子没有消失'
        Wait-Saved
        $l = Read-Layout
        foreach ($k in @($KA, $KB)) {
            $s = ($l.FreeIcons.PSObject.Properties | Where-Object { $_.Name -eq $k }).Value
            Assert-True ($null -ne $s -and $null -eq $s.LastSeenUtc) "$k 没有回到自由区"
            $dist = [Math]::Abs($s.Col - $col0) + [Math]::Abs($s.Row - $row0)
            Assert-True ($dist -le 4) "$k 离原位置太远：($($s.Col),$($s.Row)) vs ($col0,$row0)"
        }
        Move-Mouse 2200 1250 200
        Save-Screen -Name b-b11-dissolved -Rect @(([int](1200)), ([int](200)), ([int](1200)), ([int](1000))) | Out-Null
        "a,b 回到自由区（格子原位 col=$col0 row=$row0）"
    }
}
finally { Finish-BoxTest }
$fails = Show-Summary
exit $fails
