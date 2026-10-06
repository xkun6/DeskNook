. $PSScriptRoot\common.ps1
Stop-DeskNook | Out-Null
Remove-TestFiles
New-TestFile -Name xk-test-a.txt -Content "hello" | Out-Null
New-TestFile -Name xk-test-b.txt -Content "hello2" | Out-Null
Wait-Ms 1000
Minimize-All
$dd = [Environment]::GetFolderPath('Desktop')
try {
  Start-DeskNook | Out-Null; Wait-Ms 1500
  $a = Get-IconCenter 'xk-test-a.txt'; $b = Get-IconCenter 'xk-test-b.txt'
  "a=$($a.X),$($a.Y) b=$($b.X),$($b.Y)"
  # 单击选中
  Click-Mouse $a.X $a.Y; Wait-Ms 500; Save-Screen -Name s4-click | Out-Null
  # Ctrl 加选
  Click-Mouse $b.X $b.Y -Ctrl; Wait-Ms 500; Save-Screen -Name s4-ctrl | Out-Null
  # 框选(空白处拖到两个图标)
  Click-Mouse 1500 700; Wait-Ms 300
  Drag-Mouse 200 1100 20 (($b.Y)+60); Wait-Ms 500; Save-Screen -Name s4-band | Out-Null
  # F2 重命名
  Click-Mouse $a.X $a.Y; Wait-Ms 300
  Press-Key F2; Wait-Ms 600; Save-Screen -Name s4-rename-box | Out-Null
  Press-Key A -Ctrl; Type-Text "xk-test-renamed.txt"; Press-Key Enter; Wait-Ms 1500
  Assert-True (Test-Path (Join-Path $dd 'xk-test-renamed.txt')) 'F2 重命名后磁盘文件名已变'
  "rename ok: $(Test-Path (Join-Path $dd 'xk-test-renamed.txt'))"
  Save-Screen -Name s4-renamed | Out-Null
  # 双击打开
  $r = Get-IconCenter 'xk-test-renamed.txt'
  DoubleClick-Mouse $r.X $r.Y; Wait-Ms 2500
  $np = Get-Process notepad -ErrorAction SilentlyContinue
  "notepad opened: $([bool]$np)"; Save-Screen -Name s4-open | Out-Null
  Stop-ProcessByName notepad
  Minimize-All; Wait-Ms 800
  # Del 删除 b
  Click-Mouse $b.X $b.Y; Wait-Ms 300
  Press-Key Delete; Wait-Ms 1200; Save-Screen -Name s4-del-dialog | Out-Null
  "b exists after Del: $(Test-Path (Join-Path $dd 'xk-test-b.txt'))"
  if (Test-Path (Join-Path $dd 'xk-test-b.txt')) { Press-Key Enter; Wait-Ms 1200 }
  "b exists after confirm: $(Test-Path (Join-Path $dd 'xk-test-b.txt'))"
  Save-Screen -Name s4-deleted | Out-Null
  # 拖动换位置
  $r = Get-IconCenter 'xk-test-renamed.txt'
  Drag-Mouse $r.X $r.Y 700 500; Wait-Ms 1200; Save-Screen -Name s4-dragged | Out-Null
  Wait-Ms 800
  $before = Get-IconCenter 'xk-test-renamed.txt'; "after drag slot: col $($before.Col) row $($before.Row)"
  Stop-DeskNook | Out-Null; Wait-Ms 1000
  Start-DeskNook | Out-Null; Wait-Ms 1500
  $after = Get-IconCenter 'xk-test-renamed.txt'; "after restart slot: col $($after.Col) row $($after.Row)"
  Save-Screen -Name s4-restarted | Out-Null
} finally { Stop-DeskNook | Out-Null; Stop-ProcessByName notepad; Restore-All }
