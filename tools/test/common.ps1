# 测试公共辅助：读取 layout.json 得到图标屏幕坐标（主显示器，工作区原点 (0,0)，格子 75x100，图标 48）
. $PSScriptRoot\lib.ps1
$Script:LayoutPath = Join-Path $env:APPDATA 'XkDesk\layout.json'

function Get-IconCenter {
    param([Parameter(Mandatory)][string]$Name)
    $layout = Get-Content $Script:LayoutPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $slot = $null
    foreach ($p in $layout.FreeIcons.PSObject.Properties) {
        if ($p.Name -like "*\$Name") { $slot = $p.Value; break }
    }
    if (-not $slot) { throw "layout.json 中没有 $Name" }
    if ($slot.Monitor -notlike '*DISPLAY1') { throw "$Name 不在主显示器：$($slot.Monitor)" }
    return @{ X = [int]($slot.Col * 75 + 37); Y = [int]($slot.Row * 100 + 28); Col = $slot.Col; Row = $slot.Row }
}

# 在空白处找一个远离图标的点（主显示器右侧中部）
function Get-BlankPoint { return @{ X = 1500; Y = 700 } }
