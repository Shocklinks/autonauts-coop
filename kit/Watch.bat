@echo off
REM AutonautsCoop test kit - Watch.bat
REM Version 0.12.5 - 2026-10-22 - Created with Claude
REM Leave this running: it runs the test kit whenever a request file appears in kit\requests (see watch.ps1).
REM It only ever runs the test kit. Close the window to stop.
title AutonautsCoop test watcher
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0watch.ps1"
echo.
pause
