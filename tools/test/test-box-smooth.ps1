. $PSScriptRoot\box-lib.ps1
# 格子逐像素移动/缩放测试：位移与鼠标一致、边缘吸附（<=8 吸附，>10 不吸附）、非整格宽度排布、拖动期间不重建桌面。输出截图前缀 s-
$JsonBak = Join-Path $env:TEMP 'xk-json-backup-smooth'
if (Test-Path $JsonBak) { Remove-Item $JsonBak -Recurse -Force }
New-Item -ItemType Directory -Path $JsonBak | Out-Null
Get-ChildItem $Script:DataDir -Filter *.json -ErrorAction SilentlyContinue | Copy-Item -Destination $JsonBak -Force

# 从 (x1,y1) 按住左键，逐步拖到 (x1+dx,y1+dy) 并停住；可截图；然后松开
function Drag-Exact {
    param([int]$X1, [int]$Y1, [int]$Dx, [int]$Dy, [string]$Shot, [int[]]$ShotRect, [int]$Steps = 8)
    Move-Mouse $X1 $Y1 120
    [DnTest.Native]::Mouse(0x2); Wait-Ms 120
    for ($i = 1; $i -le $Steps; $i++) {
        [void][DnTest.Native]::SetCursorPos([int]($X1 + $Dx * $i / $Steps), [int]($Y1 + $Dy * $i / $Steps)); Wait-Ms 20
    }
    [void][DnTest.Native]::SetCursorPos($X1 + $Dx, $Y1 + $Dy)
    Wait-Ms 300
    if ($Shot) { Save-Screen -Name $Shot -Rect $ShotRect | Out-Null }
    [DnTest.Native]::Mouse(0x4); Wait-Ms 400
    Wait-Saved
}
function Near { param($a, $b, $tol = 1) return ([Math]::Abs([double]$a - [double]$b) -le $tol) }

Start-BoxTest -Files @('a', 'b', 'c')
try {
    Invoke-Test 'S01 新建格子' {
        Click-ContextMenu 1500 700 -Path @('桌面整理 ▸ 新建格子')
        Assert-True (Wait-Layout { param($l) @($l.Boxes).Count -eq 1 }) '没有新格子'
        Wait-Ms 600; Wait-Saved
        # 先把格子挪到屏幕中部（远离工作区边缘与桌面图标），避免边缘吸附干扰后续用例
        $r = (Get-Boxes)[0].Rect
        Drag-Exact ([int]($r.X + 100)) ([int]($r.Y + 16)) ([int](1000 - $r.X)) ([int](500 - $r.Y))
        $r = (Get-Boxes)[0].Rect
        "Rect=$($r.X),$($r.Y) $($r.W)x$($r.H)"
    }

    Invoke-Test 'S02 标题栏拖动 +5/+13/+37 逐像素' {
        $out = @()
        foreach ($d in @(@(5, 3), @(13, -7), @(37, 21))) {
            $b = (Get-Boxes)[0].Rect
            Drag-Exact ([int]($b.X + 100)) ([int]($b.Y + 16)) $d[0] $d[1]
            $n = (Get-Boxes)[0].Rect
            Assert-True ((Near ($n.X - $b.X) $d[0]) -and (Near ($n.Y - $b.Y) $d[1])) "位移不一致：期望 $($d[0]),$($d[1]) 实际 $($n.X - $b.X),$($n.Y - $b.Y)"
            Assert-True (($n.X % 75) -ne 0) "X 仍是网格倍数：$($n.X)"
            $out += "($($d[0]),$($d[1]))->($($n.X - $b.X),$($n.Y - $b.Y))"
        }
        $out -join ' '
    }

    Invoke-Test 'S03 右下角缩放 +7/+19 逐像素' {
        $out = @()
        foreach ($d in @(@(7, 19), @(12, 5))) {
            $b = (Get-Boxes)[0].Rect
            Drag-Exact ([int]($b.X + $b.W - 2)) ([int]($b.Y + $b.H - 2)) $d[0] $d[1]
            $n = (Get-Boxes)[0].Rect
            Assert-True ((Near ($n.W - $b.W) $d[0]) -and (Near ($n.H - $b.H) $d[1])) "尺寸变化不一致：期望 $($d[0]),$($d[1]) 实际 $($n.W - $b.W),$($n.H - $b.H)"
            Assert-True ((Near $n.X $b.X 0.01) -and (Near $n.Y $b.Y 0.01)) '左上角变了'
            $out += "($($d[0]),$($d[1]))->($($n.W - $b.W),$($n.H - $b.H))"
        }
        $out -join ' '
    }

    Invoke-Test 'S04 左/上边缘缩放：对侧边不动' {
        $b = (Get-Boxes)[0].Rect
        Drag-Exact ([int]($b.X + 2)) ([int]($b.Y + $b.H / 2)) 11 0
        $n = (Get-Boxes)[0].Rect
        Assert-True (Near ($n.X + $n.W) ($b.X + $b.W)) "右边动了：$($b.X + $b.W) -> $($n.X + $n.W)"
        Assert-True (Near ($n.X - $b.X) 11) "左边位移不对：$($n.X - $b.X)"
        Assert-True (Near ($b.W - $n.W) 11) "宽度变化不对：$($b.W - $n.W)"
        $b = $n
        Drag-Exact ([int]($b.X + $b.W / 2)) ([int]($b.Y + 1)) 0 -9
        $n = (Get-Boxes)[0].Rect
        Assert-True (Near ($n.Y + $n.H) ($b.Y + $b.H)) "下边动了：$($b.Y + $b.H) -> $($n.Y + $n.H)"
        Assert-True (Near ($b.Y - $n.Y) 9) "上边位移不对：$($b.Y - $n.Y)"
        "左边 +11 右边不动；上边 -9 下边不动"
    }

    Invoke-Test 'S05 靠近另一格子边缘 5px 吸附并出辅助线；12px 不吸附' {
        Click-ContextMenu 700 250 -Path @('桌面整理 ▸ 新建格子')
        Assert-True (Wait-Layout { param($l) @($l.Boxes).Count -eq 2 }) '没有第二个格子'
        Wait-Ms 600; Wait-Saved
        $b2 = (Get-Boxes)[1].Rect
        $right2 = $b2.X + $b2.W
        $b1 = (Get-Boxes)[0].Rect
        # 先让 box1 左边落到 box2 右边 +60 处（y 与 box2 错开，不触发纵向吸附）
        Drag-Exact ([int]($b1.X + 100)) ([int]($b1.Y + 16)) ([int]($right2 + 60 - $b1.X)) 0
        $b1 = (Get-Boxes)[0].Rect
        $dx = [int]($right2 + 5 - $b1.X)
        Drag-Exact ([int]($b1.X + 100)) ([int]($b1.Y + 16)) $dx 0 -Shot 's-snap-5px' -ShotRect @(500, 150, 1100, 900)
        $n = (Get-Boxes)[0].Rect
        Assert-True (Near $n.X $right2 0.01) "5px 内没有吸附：$($n.X) vs $right2"
        Save-Screen -Name s-snap-5px-after -Rect @(500, 150, 1100, 900) | Out-Null
        $b1 = $n
        Drag-Exact ([int]($b1.X + 100)) ([int]($b1.Y + 16)) 60 0
        $b1 = (Get-Boxes)[0].Rect
        $dx = [int]($right2 + 12 - $b1.X)
        Drag-Exact ([int]($b1.X + 100)) ([int]($b1.Y + 16)) $dx 0 -Shot 's-snap-12px' -ShotRect @(500, 150, 1100, 900)
        $n = (Get-Boxes)[0].Rect
        Assert-True (Near $n.X ($right2 + 12)) "12px 外不应吸附：$($n.X) vs $($right2 + 12)"
        "5px 吸附到 $right2；12px 保持 $($n.X)"
    }

    Invoke-Test 'S06 非整格宽度下图标均匀排布（截图）' {
        foreach ($nme in @('xk-test-a.txt', 'xk-test-b.txt', 'xk-test-c.txt')) {
            $p = Get-IconCenter $nme
            $bx = (Get-Boxes)[0]
            Drag-Mouse $p.X $p.Y ([int]($bx.Rect.X + 150)) ([int]($bx.Rect.Y + 130)); Wait-Ms 700
        }
        Assert-True (Wait-Layout { param($l) @($l.Boxes[0].ItemKeys).Count -eq 3 }) '图标没进格子'
        Wait-Saved
        $b = (Get-Boxes)[0].Rect
        $target = 3 * 75 + 37
        Drag-Exact ([int]($b.X + $b.W - 2)) ([int]($b.Y + $b.H - 2)) ([int]($target - $b.W)) 0
        $n = (Get-Boxes)[0].Rect
        Assert-True (Near $n.W $target) "宽度不对：$($n.W)"
        Move-Mouse 2000 1250 300
        Save-Screen -Name s-nonwhole-width -Rect @(([int]$n.X - 30), ([int]$n.Y - 30), ([int]$n.W + 60), ([int]$n.H + 60)) | Out-Null
        "W=$($n.W) cols=$([int][Math]::Floor($n.W / 75))"
    }

    Invoke-Test 'S07 拖动平滑度：60 步 MouseMove，期间不重建桌面' {
        $b = (Get-Boxes)[0].Rect
        $proc = Get-Process -Name DeskNext
        $cpu0 = $proc.TotalProcessorTime.TotalMilliseconds
        $mark = Get-LogMark
        $x1 = [int]($b.X + 100); $y1 = [int]($b.Y + 16)
        Move-Mouse $x1 $y1 150
        [DnTest.Native]::Mouse(0x2); Wait-Ms 100
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $per = New-Object System.Collections.Generic.List[double]
        for ($i = 1; $i -le 60; $i++) {
            $t = $sw.Elapsed.TotalMilliseconds
            [void][DnTest.Native]::SetCursorPos($x1 - $i, $y1 + [int]($i / 2))
            $per.Add($sw.Elapsed.TotalMilliseconds - $t)
            Start-Sleep -Milliseconds 16
        }
        $total = $sw.Elapsed.TotalMilliseconds
        Wait-Ms 200
        $rebuildsDuring = @(Get-LogSince $mark | Where-Object { $_ -match '桌面重建' }).Count
        [DnTest.Native]::Mouse(0x4); Wait-Ms 600
        $proc.Refresh()
        $cpu1 = $proc.TotalProcessorTime.TotalMilliseconds
        Assert-True ($rebuildsDuring -eq 0) "拖动期间重建了 $rebuildsDuring 次"
        $m = ($per | Measure-Object -Average -Maximum)
        "60 步总耗时 $([Math]::Round($total))ms；SetCursorPos 平均 $([Math]::Round($m.Average,2))ms 最大 $([Math]::Round($m.Maximum,2))ms；DeskNext CPU +$([Math]::Round($cpu1 - $cpu0))ms；拖动期重建=$rebuildsDuring"
    }
}
finally {
    Finish-BoxTest
    Get-ChildItem $JsonBak -Filter *.json | Copy-Item -Destination $Script:DataDir -Force
    Remove-Item $JsonBak -Recurse -Force -ErrorAction SilentlyContinue
}
$fails = Show-Summary
exit $fails
