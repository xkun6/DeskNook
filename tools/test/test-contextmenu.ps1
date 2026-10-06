. $PSScriptRoot\common.ps1
Stop-DeskNext | Out-Null
Remove-TestFiles
New-TestFile -Name xk-test-a.txt -Content "hello" | Out-Null
New-TestFile -Name xk-test-b.txt -Content "hello2" | Out-Null
Wait-Ms 1500
Minimize-All
try {
  # 先启动一次导入位置，再退出，用于计算坐标
  $m = Start-DeskNext; Wait-Ms 1000; Stop-DeskNext | Out-Null; Wait-Ms 1500
  $a = Get-IconCenter 'xk-test-a.txt'
  "pos a = $($a.X),$($a.Y)  (col $($a.Col) row $($a.Row))"
  # C：原生
  Click-Mouse $a.X $a.Y -Button Right; Wait-Ms 800
  Save-Screen -Name C-native-itemmenu | Out-Null
  Press-Key Escape; Wait-Ms 500
  $b = Get-BlankPoint
  Click-Mouse $b.X $b.Y -Button Right; Wait-Ms 800
  Save-Screen -Name C-native-bgmenu | Out-Null
  Press-Key Escape; Wait-Ms 500
  # D：我们的
  Start-DeskNext | Out-Null; Wait-Ms 1500
  Click-Mouse $a.X $a.Y -Button Right; Wait-Ms 1000
  Save-Screen -Name D-xk-itemmenu | Out-Null
  Press-Key Escape; Wait-Ms 500
  Click-Mouse $b.X $b.Y -Button Right; Wait-Ms 1000
  Save-Screen -Name D-xk-bgmenu | Out-Null
  Press-Key Escape; Wait-Ms 500
} finally {
  Stop-DeskNext | Out-Null
  Restore-All
}
