@echo off
REM AutonautsCoop test kit - Watch.bat
REM Version 0.12.15 - 2026-10-23 - Created with Claude
REM Leave this running: it runs the test kit whenever a request file appears in kit\requests (see watch.ps1).
REM It only ever runs the test kit (and, when asked with action=shutdown, shuts the PC down). Close the window to stop.
title AutonautsCoop test watcher
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0watch.ps1"
if errorlevel 1 pause
