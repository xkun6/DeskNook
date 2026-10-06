. $PSScriptRoot\box-lib.ps1
Stop-DeskNext | Out-Null
Backup-Layout
Minimize-All
try {
  Start-DeskNext | Out-Null; Wait-Ms 1500
  $b = Get-BlankPoint
  Click-ContextMenu $b.X $b.Y -Path @('桌面整理 ▸ 新建格子')
  Wait-Ms 1000
  Save-Screen -Name b1-newbox -Rect @(1200,500,1000,600) | Out-Null
  Wait-Saved
  Get-Boxes | ConvertTo-Json -Depth 5
} finally {
  Press-Key Escape
  Stop-DeskNext | Out-Null; Restore-Layout; Restore-All
}
