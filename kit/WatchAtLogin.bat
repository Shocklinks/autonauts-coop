@echo off
REM AutonautsCoop test kit - WatchAtLogin.bat
REM Version 0.12.5 - 2026-10-22 - Created with Claude
REM Adds (or with "remove": removes) a shortcut in your Startup folder so Watch.bat starts when you log in.
set LNK=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\AutonautsCoop test watcher.lnk
if /i "%~1"=="remove" (
  del "%LNK%" 2>nul
  echo Removed: the watcher no longer starts at login.
  pause
  exit /b
)
powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%LNK%'); $s.TargetPath='%~dp0Watch.bat'; $s.WorkingDirectory='%~dp0'; $s.WindowStyle=7; $s.Save()"
echo Added: the watcher starts (minimized) when you log in. Run "WatchAtLogin.bat remove" to undo.
pause
