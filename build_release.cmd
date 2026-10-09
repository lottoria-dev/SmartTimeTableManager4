@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build-Release.ps1" %*
exit /b %ERRORLEVEL%
