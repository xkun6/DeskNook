. $PSScriptRoot\common.ps1
Stop-DeskNook | Out-Null
Remove-TestFiles
New-TestFile -Name xk-test-a.txt | Out-Null
Wait-Ms 1000
Minimize-All
try {
  Start-DeskNook | Out-Null; Wait-Ms 1500
  $a = Get-IconCenter 'xk-test-a.txt'
  $pr = Start-Process cmd.exe -ArgumentList '/k','title xk-test-win' -PassThru; Wait-Ms 1500
  Save-Screen -Name s6-cmd | Out-Null
  Click-Mouse 1500 1000; Wait-Ms 800   # 点击桌面空白（cmd 窗口若在这里则会点到它）
  Click-Mouse $a.X $a.Y; Wait-Ms 800
  Save-Screen -Name s6-after-click | Out-Null
  Stop-Process -Id $pr.Id -Force; Wait-Ms 500
  Restore-All; Wait-Ms 500
  Send-WinD; Wait-Ms 1500; Save-Screen -Name s6-wind | Out-Null
  Send-WinD; Wait-Ms 1200
  Minimize-All; Wait-Ms 800
  # Explorer 重启
  $mark = Get-LogMark
  Stop-ProcessByName explorer; Wait-Ms 1500; Start-Process explorer.exe
  $hit = Wait-Log -Pattern '重建宿主窗口（Explorer 重启后重挂）' -Since $mark -TimeoutSec 30
  "reattach: $([bool]$hit)"; Wait-Ms 3000
  Minimize-All; Wait-Ms 1000
  Save-Screen -Name s6-explorer-restart | Out-Null
} finally { Stop-DeskNook | Out-Null; Restore-All }
