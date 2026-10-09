@echo off
setlocal
pushd "%~dp0"
if not exist artifacts mkdir artifacts
dotnet build tests\TimeTableManager.Tests\TimeTableManager.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false
if errorlevel 1 goto failed
dotnet run --project tests\TimeTableManager.Tests\TimeTableManager.Tests.csproj -c Release --no-build -- samples artifacts\test-results.json
if errorlevel 1 goto failed
popd
exit /b 0
:failed
popd
exit /b 1
