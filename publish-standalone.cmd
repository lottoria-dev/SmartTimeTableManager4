@echo off
setlocal
pushd "%~dp0"
dotnet publish src\TimeTableManager.App\TimeTableManager.App.csproj -p:PublishProfile=Standalone-win-x64 -c Release -r win-x64 --self-contained true
set "publish_exit=%ERRORLEVEL%"
if "%publish_exit%"=="0" echo Output: artifacts\standalone\win-x64\TimeTableManager.exe
popd
exit /b %publish_exit%
