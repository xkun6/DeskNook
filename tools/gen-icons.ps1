# 生成应用图标与菜单线条图标（多尺寸 ICO，PNG 帧）。输出到 src/DeskNook/Assets/。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools/gen-icons.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\DeskNook\Assets'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$out = (Resolve-Path $out).Path

function New-Bmp([int]$s, [scriptblock]$draw) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    & $draw $g $s
    $g.Dispose()
    return $bmp
}
function RR([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}
function Write-Ico([string]$name, [int[]]$sizes, [scriptblock]$draw) {
    $frames = @()
    foreach ($s in $sizes) {
        $bmp = New-Bmp $s $draw
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        $frames += ,@($s, $ms.ToArray())
    }
    $fs = [System.IO.File]::Create((Join-Path $out $name))
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
    $off = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $s = $f[0]; $d = $f[1]
        $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s })); $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
        $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$d.Length); $bw.Write([uint32]$off); $off += $d.Length
    }
    foreach ($f in $frames) { $bw.Write($f[1]) }
    $bw.Dispose(); $fs.Dispose()
}

$blue = [System.Drawing.Color]::FromArgb(255, 77, 163, 255)
function Pen-For($s, [float]$w = 1.4) {
    $p = New-Object System.Drawing.Pen $blue, ([Math]::Max(1.0, $w * $s / 16.0))
    $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p.LineJoin = 'Round'
    return $p
}
# 所有线条图标按 16x16 坐标绘制，等比放大
function Scale($g, $s) { $g.ScaleTransform($s / 16.0, $s / 16.0) }
function Line-Pen { $p = New-Object System.Drawing.Pen $blue, 1.4; $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p.LineJoin = 'Round'; return $p }

$menuSizes = 16, 20, 24, 32, 48

# 应用图标：蓝色圆角方块 + 2x2 白色格子
$app = {
    param($g, $s)
    Scale $g $s
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 16, 16), ([System.Drawing.Color]::FromArgb(255, 77, 163, 255)), ([System.Drawing.Color]::FromArgb(255, 37, 99, 235)))
    $g.FillPath($bg, (RR 0.5 0.5 15 15 3.6))
    $w = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $w2 = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 255, 255, 255))
    $g.FillPath($w, (RR 3 3 4.4 4.4 1.2)); $g.FillPath($w2, (RR 8.6 3 4.4 4.4 1.2))
    $g.FillPath($w2, (RR 3 8.6 4.4 4.4 1.2)); $g.FillPath($w, (RR 8.6 8.6 4.4 4.4 1.2))
}
Write-Ico 'app.ico' @(16, 20, 24, 32, 48, 256) $app
Write-Ico 'menu-app.ico' $menuSizes $app

function Plus($g, $p, $cx, $cy) { $g.DrawLine($p, $cx - 2.2, $cy, $cx + 2.2, $cy); $g.DrawLine($p, $cx, $cy - 2.2, $cx, $cy + 2.2) }

Write-Ico 'box-new.ico' $menuSizes { param($g, $s) Scale $g $s; $p = Line-Pen; $g.DrawPath($p, (RR 1.5 3 10.5 10.5 2)); Plus $g $p 11.5 5 }
Write-Ico 'folder-new.ico' $menuSizes { param($g, $s) Scale $g $s; $p = Line-Pen
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddLines(@((New-Object System.Drawing.PointF 1.5, 12.5), (New-Object System.Drawing.PointF 1.5, 3.5), (New-Object System.Drawing.PointF 5.5, 3.5), (New-Object System.Drawing.PointF 7, 5.2), (New-Object System.Drawing.PointF 13, 5.2), (New-Object System.Drawing.PointF 13, 7)))
    $g.DrawPath($p, $path); $g.DrawLine($p, 1.5, 12.5, 9, 12.5); Plus $g $p 11.8 11.8 }
Write-Ico 'as-box.ico' $menuSizes { param($g, $s) Scale $g $s; $p = Line-Pen
    $g.DrawPath($p, (RR 1.8 1.8 5 5 1.2)); $g.DrawPath($p, (RR 9.2 1.8 5 5 1.2)); $g.DrawPath($p, (RR 1.8 9.2 5 5 1.2)); $g.DrawPath($p, (RR 9.2 9.2 5 5 1.2)) }
Write-Ico 'locate.ico' $menuSizes { param($g, $s) Scale $g $s; $p = Line-Pen
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddLines(@((New-Object System.Drawing.PointF 1.5, 12.5), (New-Object System.Drawing.PointF 1.5, 3.5), (New-Object System.Drawing.PointF 5.5, 3.5), (New-Object System.Drawing.PointF 7, 5.2), (New-Object System.Drawing.PointF 13.5, 5.2), (New-Object System.Drawing.PointF 13.5, 12.5), (New-Object System.Drawing.PointF 1.5, 12.5)))
    $g.DrawPath($p, $path); $g.DrawLine($p, 5, 8.9, 10.5, 8.9); $g.DrawLine($p, 8.6, 7, 10.5, 8.9); $g.DrawLine($p, 8.6, 10.8, 10.5, 8.9) }
Write-Ico 'organize.ico' $menuSizes { param($g, $s) Scale $g $s; $p = Line-Pen
    $g.DrawLine($p, 2, 3.5, 14, 3.5); $g.DrawLine($p, 2, 8, 10, 8); $g.DrawLine($p, 2, 12.5, 6.5, 12.5)
    $g.DrawLine($p, 12, 7, 12, 13); $g.DrawLine($p, 10.2, 11.2, 12, 13); $g.DrawLine($p, 13.8, 11.2, 12, 13) }
Write-Ico 'undo.ico' $menuSizes { param($g, $s) Scale $g $s; $p = Line-Pen
    $g.DrawArc($p, 3.5, 4.5, 9.5, 8, -90, 250); $g.DrawLine($p, 3.2, 2.8, 6.4, 4.6); $g.DrawLine($p, 3.2, 2.8, 3.4, 6.6) }
Write-Ico 'settings.ico' $menuSizes { param($g, $s) Scale $g $s; $p = Line-Pen
    $g.DrawEllipse($p, 5.2, 5.2, 5.6, 5.6)
    foreach ($a in 0, 45, 90, 135, 180, 225, 270, 315) { $r = $a * [Math]::PI / 180; $g.DrawLine($p, 8 + 4.2 * [Math]::Cos($r), 8 + 4.2 * [Math]::Sin($r), 8 + 6.4 * [Math]::Cos($r), 8 + 6.4 * [Math]::Sin($r)) } }
Write-Ico 'exit.ico' $menuSizes { param($g, $s) Scale $g $s; $p = Line-Pen
    $g.DrawArc($p, 2.8, 3.2, 10.4, 10.4, -55, 290); $g.DrawLine($p, 8, 1.8, 8, 7.6) }
Get-ChildItem $out | Select-Object Name, Length
