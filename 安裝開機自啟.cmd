@echo off
chcp 65001 >nul
setlocal
set "EXE=%~dp0ClaudeUsageWidget.exe"
if not exist "%EXE%" (
  echo [ERROR] ClaudeUsageWidget.exe not found next to this script.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$w=New-Object -ComObject WScript.Shell; $lnk=Join-Path ([Environment]::GetFolderPath('Startup')) 'ClaudeUsage.lnk'; $s=$w.CreateShortcut($lnk); $s.TargetPath='%EXE%'; $s.WorkingDirectory='%~dp0'; $s.Save(); Write-Host ('Startup shortcut created: ' + $lnk)"
echo Done. Launching widget now...
start "" "%EXE%"
endlocal
