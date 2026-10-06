. $PSScriptRoot\common.ps1
Stop-XkDesk | Out-Null
Remove-TestFiles
New-TestFile -Name xk-test-a.txt | Out-Null
New-TestFile -Name xk-test-b.txt | Out-Null
Wait-Ms 1000
Minimize-All
try {
  Start-XkDesk | Out-Null; Wait-Ms 1500
  $a = Get-IconCenter 'xk-test-a.txt'; $b = Get-IconCenter 'xk-test-b.txt'
  # 子菜单：发送到
  Click-Mouse $a.X $a.Y -Button Right; Wait-Ms 900
  Save-Screen -Name s5-menu | Out-Null
  Press-Key Escape; Wait-Ms 500
  # 多选右键
  Click-Mouse $a.X $a.Y; Click-Mouse $b.X $b.Y -Ctrl
  Click-Mouse $b.X $b.Y -Button Right; Wait-Ms 900; Save-Screen -Name s5-multi | Out-Null
  Press-Key Escape; Wait-Ms 500
  # Shift+右键
  Click-Mouse $a.X $a.Y -Button Right -Shift; Wait-Ms 900; Save-Screen -Name s5-shift | Out-Null
  Press-Key Escape; Wait-Ms 500
  # 焦点与 Z 序：点桌面后 F2
  Click-Mouse $a.X $a.Y; Wait-Ms 300; Press-Key F2; Wait-Ms 500; Save-Screen -Name s5-f2 | Out-Null
  Press-Key Escape; Wait-Ms 300
  # 打开记事本，确认不被盖住
  Start-Process notepad; Wait-Ms 2000; Save-Screen -Name s5-notepad | Out-Null
  Click-Mouse $a.X $a.Y; Wait-Ms 800; Save-Screen -Name s5-click-desktop-with-notepad | Out-Null
  Stop-ProcessByName notepad; Wait-Ms 500
  # Win+D
  Restore-All; Wait-Ms 500
  Send-WinD; Wait-Ms 1500; Save-Screen -Name s5-wind | Out-Null
  Send-WinD; Wait-Ms 1000
} finally { Stop-XkDesk | Out-Null; Stop-ProcessByName notepad; Restore-All }
