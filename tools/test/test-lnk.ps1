. $PSScriptRoot\common.ps1
Stop-DeskNook | Out-Null; Remove-TestFiles
$dd = [Environment]::GetFolderPath('Desktop')
$w = New-Object -ComObject WScript.Shell
$l = $w.CreateShortcut((Join-Path $dd 'xk-test-link.lnk')); $l.TargetPath = 'C:\Windows\notepad.exe'; $l.Save()
Wait-Ms 1500; Minimize-All
try {
  Start-DeskNook | Out-Null; Wait-Ms 1000; Stop-DeskNook | Out-Null; Wait-Ms 1500
  $a = Get-IconCenter 'xk-test-link.lnk'
  Save-Screen -Name l1-native | Out-Null
  Start-DeskNook | Out-Null; Wait-Ms 2000
  Save-Screen -Name l2-xk | Out-Null
  "$($a.X),$($a.Y)"
} finally { Stop-DeskNook | Out-Null; Remove-TestFiles; Restore-All }
