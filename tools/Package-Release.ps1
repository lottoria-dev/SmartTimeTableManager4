param([string]$PublishDirectory = '')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Set a stable version before packaging.' }
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $repoRoot 'artifacts\standalone\win-x64'
}
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$exePath = Join-Path $PublishDirectory 'TimeTableManager.exe'
if (-not (Test-Path -LiteralPath $exePath)) { throw 'Publish Standalone-win-x64 first.' }
$productVersion = (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($productVersion) -or $productVersion.Split('+')[0] -ne $version) {
    throw 'The executable version does not match Directory.Build.props.'
}
$output = Join-Path $repoRoot 'artifacts\release'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$zipPath = Join-Path $output "SmartTimeTableManager4-$version-win-x64.zip"
if (Test-Path -LiteralPath $zipPath) { throw 'Release ZIP already exists. Move the previous ZIP before packaging again.' }
$stage = Join-Path $output ('stage-' + $version + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
Get-ChildItem -LiteralPath $PublishDirectory -Force | Copy-Item -Destination $stage -Recurse
foreach ($name in @('LICENSE.md','LICENSE_ORIGINAL.txt','THIRD_PARTY_NOTICES.md','VERSION_HISTORY.txt')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'third-party-licenses') -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'templates') -Destination $stage -Recurse
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$line = $hash + '  ' + [IO.Path]::GetFileName($zipPath)
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), $line + "`n", [Text.UTF8Encoding]::new($false))
Write-Output $zipPath
Write-Output $line
Write-Output 'Test the ZIP in a separate folder before uploading it to GitHub Releases.'
