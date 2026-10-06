. $PSScriptRoot\common.ps1
Stop-XkDesk | Out-Null
Remove-TestFiles
New-TestFile -Name xk-test-a.txt -Content "hello" | Out-Null
$dd = [Environment]::GetFolderPath('Desktop')
Wait-Ms 1000
Minimize-All
try {
  Start-XkDesk | Out-Null; Wait-Ms 1500
  $a = Get-IconCenter 'xk-test-a.txt'
  Click-Mouse $a.X $a.Y; Wait-Ms 300
  Press-Key C -Ctrl; Wait-Ms 600
  Click-Mouse 1500 700 -Button Right; Wait-Ms 1000
  Save-Screen -Name u1-bg-paste-enabled | Out-Null
  # 粘贴项位置：菜单弹出后按键选择 P
  Press-Key P; Wait-Ms 300; Press-Key Enter; Wait-Ms 2500
  $copies = @(Get-ChildItem $dd | ? Name -like 'xk-test-*' | % Name)
  "files after paste: $($copies -join ', ')"
  $copy = $copies | ? { $_ -ne 'xk-test-a.txt' } | select -First 1
  if ($copy) {
    $c = Get-IconCenter $copy
    Click-Mouse $c.X $c.Y; Wait-Ms 300
    Press-Key Delete; Wait-Ms 1500
    "copy exists after delete: $(Test-Path (Join-Path $dd $copy))"
    Click-Mouse 1500 700 -Button Right; Wait-Ms 1000
    Save-Screen -Name u2-bg-undo | Out-Null
    Press-Key U; Wait-Ms 300; Press-Key Enter; Wait-Ms 2500
    "copy exists after undo: $(Test-Path (Join-Path $dd $copy))"
    Save-Screen -Name u3-after-undo | Out-Null
  }
} finally { Stop-XkDesk | Out-Null; Remove-TestFiles; Restore-All }
