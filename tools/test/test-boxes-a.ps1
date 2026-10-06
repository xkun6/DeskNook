. $PSScriptRoot\box-lib.ps1
# 阶段 2 测试 A：新建格子、拖入图标、移动/吸附/辅助线、缩放、重命名、折叠与悬停展开、锁定、格子间拖动与调序、滚动
Start-BoxTest -Files @('a', 'b', 'c')
$A = 'xk-test-a.txt'; $B = 'xk-test-b.txt'; $C = 'xk-test-c.txt'
$KA = Key-Path $A; $KB = Key-Path $B; $KC = Key-Path $C
try {
    Invoke-Test 'A01 新建格子' {
        Click-ContextMenu 1500 700 -Path @('xk-desk ▸ 新建格子')
        Assert-True (Wait-Layout { param($l) @($l.Boxes).Count -eq 1 }) '布局里没有新格子'
        Wait-Ms 600
        $bx = (Get-Boxes)[0]
        Assert-True ($bx.Kind -eq 'Normal' -and $bx.Name -eq '新格子') "名称/类型不对：$($bx.Name) $($bx.Kind)"
        Assert-True ($bx.Rect.X -eq 1500 -and $bx.Rect.Y -eq 700 -and $bx.Rect.W -eq 300 -and $bx.Rect.H -eq 236) "位置/大小不对：$($bx.Rect.X),$($bx.Rect.Y) $($bx.Rect.W)x$($bx.Rect.H)"
        Save-Screen -Name b-a01-newbox -Rect @(1300, 500, 800, 600) | Out-Null
        "新格子 id=$($bx.Id) rect=$($bx.Rect.X),$($bx.Rect.Y) $($bx.Rect.W)x$($bx.Rect.H)"
    }

    Invoke-Test 'A02 把桌面文件拖进格子（只改布局不动文件）' {
        Wait-Saved
        foreach ($n in @($A, $B, $C)) {
            $p = Get-IconCenter $n
            $bx = (Get-Boxes)[0]
            Drag-Mouse $p.X $p.Y ([int]($bx.Rect.X + 150)) ([int]($bx.Rect.Y + 130)); Wait-Ms 700
        }
        Assert-True (Wait-Layout { param($l) @($l.Boxes[0].ItemKeys).Count -eq 3 }) "格子内项数不是 3：$(@((Get-Boxes)[0].ItemKeys).Count)"
        $l = Read-Layout
        foreach ($k in @($KA, $KB, $KC)) {
            Assert-True (@($l.Boxes[0].ItemKeys) -contains $k) "ItemKeys 缺少 $k"
            Assert-True (-not ($l.FreeIcons.PSObject.Properties.Name -contains $k)) "$k 仍在自由区"
            Assert-True (Test-Path $k) "文件被动了：$k"
        }
        Save-Screen -Name b-a02-items -Rect @(1300, 500, 800, 600) | Out-Null
        "ItemKeys=$(@($l.Boxes[0].ItemKeys).Count)，文件均仍在桌面"
    }

    Invoke-Test 'A03 第二个格子 + 标题栏拖动吸附与对齐辅助线' {
        Click-ContextMenu 1100 300 -Path @('xk-desk ▸ 新建格子')
        Assert-True (Wait-Layout { param($l) @($l.Boxes).Count -eq 2 }) '没有第二个格子'
        Wait-Ms 600
        $b1 = (Get-Boxes)[0]; $b2 = (Get-Boxes)[1]
        "box2 rect=$($b2.Rect.X),$($b2.Rect.Y)"
        # box1 标题栏起点，向左上拖到与 box2 左边对齐（故意多偏 3px 测吸附），在 box2 下方
        $sx = [int]($b1.Rect.X + 100); $sy = [int]($b1.Rect.Y + 16)
        $dx = [int]($b2.Rect.X - $b1.Rect.X + 3); $dy = [int]($b2.Rect.Y + $b2.Rect.H + 10 - $b1.Rect.Y - 4)
        Drag-Mouse-Shot $sx $sy ($sx + $dx) ($sy + $dy) 'b-a03-guides' @(900, 200, 1100, 800)
        Wait-Saved
        $n1 = (Get-Boxes)[0]
        Assert-True ($n1.Rect.X -eq $b2.Rect.X) "X 没有对齐到 box2：$($n1.Rect.X) vs $($b2.Rect.X)"
        Assert-True ($n1.Rect.Y -eq ($b2.Rect.Y + $b2.Rect.H)) "Y 没有吸附到 box2 下边缘：$($n1.Rect.Y) vs $($b2.Rect.Y + $b2.Rect.H)"
        Save-Screen -Name b-a03-moved -Rect @(900, 200, 1100, 800) | Out-Null
        "box1 移到 $($n1.Rect.X),$($n1.Rect.Y)"
    }

    Invoke-Test 'A04 拖右下角缩放（逐像素）' {
        $bx = (Get-Boxes)[0]
        $cx = [int]($bx.Rect.X + $bx.Rect.W - 2); $cy = [int]($bx.Rect.Y + $bx.Rect.H - 2)
        Drag-Mouse-Shot $cx $cy ($cx + 80) ($cy + 110) 'b-a04-resizing' @(900, 200, 1100, 900)
        Wait-Saved
        $n = (Get-Boxes)[0]
        Assert-True ($n.Rect.W -eq ($bx.Rect.W + 80)) "宽度应 +80：$($bx.Rect.W) → $($n.Rect.W)"
        Assert-True ($n.Rect.H -eq ($bx.Rect.H + 110)) "高度应 +110：$($bx.Rect.H) → $($n.Rect.H)"
        Save-Screen -Name b-a04-resized -Rect @(900, 200, 1100, 900) | Out-Null
        "尺寸 $($bx.Rect.W)x$($bx.Rect.H) → $($n.Rect.W)x$($n.Rect.H)"
    }

    Invoke-Test 'A05 双击标题原位重命名' {
        $bx = (Get-Boxes)[0]
        DoubleClick-Mouse ([int]($bx.Rect.X + 60)) ([int]($bx.Rect.Y + 16)); Wait-Ms 600
        Save-Screen -Name b-a05-renaming -Rect @(900, 500, 800, 400) | Out-Null
        Type-Text '测试盒'; Press-Key Enter; Wait-Ms 800
        Wait-Saved
        $n = (Get-Boxes)[0]
        Assert-True ($n.Name -eq '测试盒') "名称未改：$($n.Name)"
        Save-Screen -Name b-a05-renamed -Rect @(900, 500, 800, 400) | Out-Null
        "名称=$($n.Name)"
    }

    Invoke-Test 'A06 折叠、悬停临时展开、移开收起' {
        $bx = (Get-Boxes)[0]
        $btn = @{ X = [int]($bx.Rect.X + $bx.Rect.W - 6 - 24 - 2 - 12); Y = [int]($bx.Rect.Y + 16) }
        Click-Mouse $btn.X $btn.Y; Wait-Ms 500
        Assert-True (Wait-Layout { param($l) $l.Boxes[0].Collapsed -eq $true }) '没有折叠'
        Move-Mouse 2000 1250 300; Wait-Ms 600
        Save-Screen -Name b-a06-collapsed -Rect @(1000, 500, 600, 450) | Out-Null
        Move-Mouse ([int]($bx.Rect.X + 80)) ([int]($bx.Rect.Y + 16)) 300; Wait-Ms 700
        Save-Screen -Name b-a06-hover-expanded -Rect @(1000, 500, 600, 450) | Out-Null
        Move-Mouse 2000 1250 300; Wait-Ms 900
        Save-Screen -Name b-a06-collapsed-again -Rect @(1000, 500, 600, 450) | Out-Null
        $d1 = Get-ImageDiffRatio (Join-Path $Script:OutDir 'b-a06-collapsed.png') (Join-Path $Script:OutDir 'b-a06-hover-expanded.png') @(40, 70, 400, 250) -Tolerance 60
        $d2 = Get-ImageDiffRatio (Join-Path $Script:OutDir 'b-a06-collapsed.png') (Join-Path $Script:OutDir 'b-a06-collapsed-again.png') @(40, 70, 400, 250) -Tolerance 60
        Assert-True ($d1 -gt 0.06) "悬停没有展开（差异 $d1）"
        Assert-True ($d2 -lt $d1 / 3) "移开后没有收起（差异 $d2）"
        # 点按钮展开回来
        Click-Mouse $btn.X $btn.Y; Wait-Ms 500
        Assert-True (Wait-Layout { param($l) $l.Boxes[0].Collapsed -eq $false }) '没有展开回来'
        Move-Mouse 2000 1250 300
        "悬停展开差异=$([Math]::Round($d1,3))，收起后差异=$([Math]::Round($d2,4))"
    }

    Invoke-Test 'A07 锁定后拖不动，解锁后可动' {
        $bx = (Get-Boxes)[0]
        Click-ContextMenu ([int]($bx.Rect.X + 150)) ([int]($bx.Rect.Y + 16)) -Path @('锁定')
        Assert-True (Wait-Layout { param($l) $l.Boxes[0].Locked -eq $true }) '没有锁定'
        $before = (Get-Boxes)[0].Rect
        Drag-Mouse ([int]($bx.Rect.X + 100)) ([int]($bx.Rect.Y + 16)) ([int]($bx.Rect.X + 300)) ([int]($bx.Rect.Y + 216)); Wait-Ms 600
        # 锁定时角上也不能缩放
        Drag-Mouse ([int]($bx.Rect.X + $bx.Rect.W - 2)) ([int]($bx.Rect.Y + $bx.Rect.H - 2)) ([int]($bx.Rect.X + $bx.Rect.W + 150)) ([int]($bx.Rect.Y + $bx.Rect.H + 150)); Wait-Ms 600
        Wait-Saved
        $after = (Get-Boxes)[0].Rect
        Assert-True ($before.X -eq $after.X -and $before.Y -eq $after.Y -and $before.W -eq $after.W -and $before.H -eq $after.H) "锁定后位置/大小变了：$($after.X),$($after.Y) $($after.W)x$($after.H)"
        Save-Screen -Name b-a07-locked -Rect @(900, 500, 800, 600) | Out-Null
        Click-ContextMenu ([int]($bx.Rect.X + 150)) ([int]($bx.Rect.Y + 16)) -Path @('解除锁定')
        Assert-True (Wait-Layout { param($l) $l.Boxes[0].Locked -eq $false }) '没有解除锁定'
        "锁定后位置不变 ($($after.X),$($after.Y))"
    }

    Invoke-Test 'A08 格子之间拖动图标 + 格子内调序（插入位置指示）' {
        $b1 = (Get-Boxes)[0]; $b2 = (Get-Boxes)[1]
        $order0 = @($b1.ItemKeys)
        # 格子内调序：把第 0 个拖到第 2 个的右半格（放到末尾）
        $p0 = Get-BoxIconCenter $b1 0; $p2 = Get-BoxIconCenter $b1 2
        Drag-Mouse-Shot $p0.X $p0.Y ($p2.X + 25) $p2.Y 'b-a08-reorder-insert' @(900, 500, 800, 600)
        Wait-Saved
        $o1 = @((Get-Boxes)[0].ItemKeys)
        Assert-True ($o1[2] -eq $order0[0] -and $o1[0] -eq $order0[1]) "调序不对：$($order0 -join ',') → $($o1 -join ',')"
        # 拖到另一个格子
        $p = Get-BoxIconCenter (Get-Boxes)[0] 0
        $t = @{ X = [int]($b2.Rect.X + 100); Y = [int]($b2.Rect.Y + 100) }
        Drag-Mouse $p.X $p.Y $t.X $t.Y; Wait-Ms 700; Wait-Saved
        $n1 = (Get-Boxes)[0]; $n2 = (Get-Boxes)[1]
        Assert-True (@($n1.ItemKeys).Count -eq 2 -and @($n2.ItemKeys).Count -eq 1) "格子间移动后数量不对：$(@($n1.ItemKeys).Count)/$(@($n2.ItemKeys).Count)"
        Assert-True ($o1[0] -eq $n2.ItemKeys[0] -or @($n2.ItemKeys) -contains $o1[0]) '移动的图标不在目标格子'
        foreach ($k in @($KA, $KB, $KC)) { Assert-True (Test-Path $k) "文件被动了：$k" }
        Save-Screen -Name b-a08-between -Rect @(900, 200, 1100, 800) | Out-Null
        "box1=$(@($n1.ItemKeys).Count) 项，box2=$(@($n2.ItemKeys).Count) 项，文件未动"
    }

    Invoke-Test 'A09 格子拖到自由区 → 图标回到自由区；再放回' {
        $b2 = (Get-Boxes)[1]
        $p = Get-BoxIconCenter $b2 0
        Drag-Mouse $p.X $p.Y 2000 600; Wait-Ms 700; Wait-Saved
        $l = Read-Layout
        Assert-True (@($l.Boxes[1].ItemKeys).Count -eq 0) '图标仍在格子里'
        $free = @($l.FreeIcons.PSObject.Properties | Where-Object { $_.Name -like '*xk-test-*' -and $null -eq $_.Value.LastSeenUtc })
        Assert-True ($free.Count -eq 1) "自由区测试图标数应为 1：$($free.Count)"
        Save-Screen -Name b-a09-tofree -Rect @(900, 200, 1500, 800) | Out-Null
        "自由区位置 col=$($free[0].Value.Col) row=$($free[0].Value.Row)"
    }

    Invoke-Test 'A10 内容超出时可滚动（细滚动条）' {
        $b1 = (Get-Boxes)[0]
        # 缩到最小 2 列 x 1 行（两个图标 → 溢出需要滚动）；把自由区那个也拖回来凑 3 个
        $cx = [int]($b1.Rect.X + $b1.Rect.W - 2); $cy = [int]($b1.Rect.Y + $b1.Rect.H - 2)
        Drag-Mouse $cx $cy ($cx - 400) ($cy - 400); Wait-Ms 700; Wait-Saved
        $s = (Get-Boxes)[0]
        Assert-True ($s.Rect.W -eq 150 -and $s.Rect.H -eq 136) "最小尺寸不对：$($s.Rect.W)x$($s.Rect.H)"
        $l = Read-Layout
        $free = @($l.FreeIcons.PSObject.Properties | Where-Object { $_.Name -like '*xk-test-*' -and $null -eq $_.Value.LastSeenUtc })[0]
        $fp = Get-IconCenter ([IO.Path]::GetFileName($free.Name))
        Drag-Mouse $fp.X $fp.Y ([int]($s.Rect.X + 70)) ([int]($s.Rect.Y + 80)); Wait-Ms 700; Wait-Saved
        Assert-True (@((Get-Boxes)[0].ItemKeys).Count -eq 3) '格子里不是 3 项'
        Move-Mouse 2000 1250 200; Wait-Ms 300
        $rect = @(([int]$s.Rect.X - 20), ([int]$s.Rect.Y - 20), 220, 200)
        Save-Screen -Name b-a10-scroll0 -Rect $rect | Out-Null
        Scroll-Wheel ([int]($s.Rect.X + 70)) ([int]($s.Rect.Y + 80)) -240
        Save-Screen -Name b-a10-scroll1 -Rect $rect | Out-Null
        $d = Get-ImageDiffRatio (Join-Path $Script:OutDir 'b-a10-scroll0.png') (Join-Path $Script:OutDir 'b-a10-scroll1.png') @(0, 0, 220, 200)
        Assert-True ($d -gt 0.03) "滚动后画面没变化（差异 $d）"
        "最小尺寸 150x136，3 项，滚动后差异=$([Math]::Round($d,3))"
    }
}
finally { Finish-BoxTest }
$fails = Show-Summary
exit $fails
