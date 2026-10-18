@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Export-PublicSource.ps1" %*
exit /b %ERRORLEVEL%
