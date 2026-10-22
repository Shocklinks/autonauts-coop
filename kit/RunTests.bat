@echo off
REM AutonautsCoop test kit - RunTests.bat
REM Version 0.12.1 - 2026-10-21 - Created with Claude
REM Double-click to run. Runs two copies of Autonauts on this PC (host + joiner) with the mod's test bots.
REM Your Steam install and your saves are only read, never changed.
title AutonautsCoop test kit
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kit.ps1" %*
echo.
pause
