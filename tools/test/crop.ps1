param([string]$Src, [string]$Dst, [int]$X, [int]$Y, [int]$W, [int]$H)
. $PSScriptRoot\lib.ps1
$s = if ([IO.Path]::IsPathRooted($Src)) { $Src } else { Join-Path $Script:OutDir $Src }
$d = if ([IO.Path]::IsPathRooted($Dst)) { $Dst } else { Join-Path $Script:OutDir $Dst }
Crop-Image -Src $s -Dst $d -X $X -Y $Y -W $W -H $H | Out-Null
Write-Output $d
