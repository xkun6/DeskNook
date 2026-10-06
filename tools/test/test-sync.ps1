. $PSScriptRoot\common.ps1
Stop-DeskNext | Out-Null
Remove-TestFiles
New-TestFile -Name xk-test-a.txt -Content "hello" | Out-Null
Wait-Ms 1000
Minimize-All
try {
  $m = Start-DeskNext; Wait-Ms 1500
  $mark = Get-LogMark
  $p = New-TestFile -Name xk-test-new.txt
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $hit = Wait-Log -Pattern '新增 1' -Since $mark -TimeoutSec 5
  "create sync: $($sw.ElapsedMilliseconds) ms  -> $hit"
  Wait-Ms 600; Save-Screen -Name s3-created | Out-Null
  $mark = Get-LogMark
  Rename-Item $p (Join-Path (Split-Path $p) 'xk-test-renamed.txt'); $sw.Restart()
  $hit = Wait-Log -Pattern '改名 1' -Since $mark -TimeoutSec 5
  "rename sync: $($sw.ElapsedMilliseconds) ms -> $hit"
  Wait-Ms 600; Save-Screen -Name s3-renamed | Out-Null
  $mark = Get-LogMark
  Remove-Item (Join-Path (Split-Path $p) 'xk-test-renamed.txt'); $sw.Restart()
  $hit = Wait-Log -Pattern '删除 1' -Since $mark -TimeoutSec 5
  "delete sync: $($sw.ElapsedMilliseconds) ms -> $hit"
  Wait-Ms 600; Save-Screen -Name s3-deleted | Out-Null
} finally { Stop-DeskNext | Out-Null; Restore-All }
