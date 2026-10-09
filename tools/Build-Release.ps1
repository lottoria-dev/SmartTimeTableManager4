param(
    [switch]$SkipPublish,
    [string]$PublishDirectory = '',
    [switch]$Force,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$buildExitCode = 0
$workDirectory = $null
try {
    Write-Host 'Smart Timetable Manager 4 - Release builder'
    if (-not $SkipPublish) {
        if (-not [string]::IsNullOrWhiteSpace($PublishDirectory)) {
            throw 'Use -SkipPublish with -PublishDirectory, or omit -PublishDirectory to build.'
        }
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw 'Install the .NET 10 SDK / Visual Studio .NET desktop development workload first.'
        }
        $workDirectory = Join-Path $repoRoot ('artifacts\build-work\' + [guid]::NewGuid().ToString('N'))
        $PublishDirectory = Join-Path $workDirectory 'publish'
        New-Item -ItemType Directory -Path $PublishDirectory -Force | Out-Null
        $project = Join-Path $repoRoot 'src\TimeTableManager.App\TimeTableManager.App.csproj'
        Write-Host '[1/2] Publishing self-contained Windows x64 executable...'
        & dotnet publish $project -p:PublishProfile=Standalone-win-x64 -c Release -r win-x64 --self-contained true --output $PublishDirectory
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE). No release was packaged." }
    }
    Write-Host '[2/2] Packaging README, licenses, history, templates and SHA-256...'
    & (Join-Path $PSScriptRoot 'Package-Release.ps1') -PublishDirectory $PublishDirectory -Force:$Force
    Write-Host 'Completed. Output: artifacts\release'
    Write-Host 'Extract the release ZIP in a separate folder and test it before uploading.'
}
catch {
    $buildExitCode = 1
    Write-Host ('FAILED: ' + $_.Exception.Message) -ForegroundColor Red
}
finally {
    if ($workDirectory -and (Test-Path -LiteralPath $workDirectory)) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }
    if (-not $NoPause) { [void](Read-Host 'Press Enter to close') }
}
exit $buildExitCode
