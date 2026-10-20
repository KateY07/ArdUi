@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
set "code=%errorlevel%"
if not "%code%"=="0" if not defined ONESETUP_SHELL pause
exit /b %code%
