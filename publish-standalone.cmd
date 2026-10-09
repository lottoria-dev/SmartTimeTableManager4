@echo off
setlocal
call "%~dp0build_release.cmd" %*
exit /b %ERRORLEVEL%
