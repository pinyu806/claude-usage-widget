@echo off
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$lnk=Join-Path ([Environment]::GetFolderPath('Startup')) 'ClaudeUsage.lnk'; if(Test-Path $lnk){ Remove-Item $lnk -Force; Write-Host ('Removed: ' + $lnk) } else { Write-Host 'No startup shortcut found.' }; $p=Get-Process ClaudeUsageWidget -ErrorAction SilentlyContinue; if($p){ $p | Stop-Process -Force; Write-Host 'Widget stopped.' }"
pause
