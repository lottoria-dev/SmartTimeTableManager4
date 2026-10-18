param([string]$PublishDirectory = '', [switch]$Force)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Encoding UTF8 -Raw
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-preview\.\d+)?$') { throw 'Set a stable or preview version before packaging.' }
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $repoRoot 'artifacts\standalone\win-x64'
}
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$exePath = Join-Path $PublishDirectory 'TimeTableManager.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Publish Standalone-win-x64 first.' }
$productVersion = (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($productVersion) -or $productVersion.Split('+')[0] -ne $version) {
    throw 'The executable version does not match Directory.Build.props.'
}
$documents = @(
    @{ Source = 'RELEASE_README.txt'; Target = 'README.txt' },
    @{ Source = 'LICENSE.md'; Target = 'LICENSE.txt' },
    @{ Source = 'LICENSE_ORIGINAL.txt'; Target = 'LICENSE_ORIGINAL.txt' },
    @{ Source = 'THIRD_PARTY_NOTICES.md'; Target = 'THIRD_PARTY_NOTICES.md' },
    @{ Source = 'VERSION_HISTORY.txt'; Target = 'VERSION_HISTORY.txt' }
)
foreach ($document in $documents) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $document.Source) -PathType Leaf)) {
        throw ('Required document is missing: ' + $document.Source)
    }
}
$templates = Join-Path $repoRoot 'templates'
$templateFiles = @(Get-ChildItem -LiteralPath $templates -File)
if (@($templateFiles | Where-Object Extension -eq '.xlsx').Count -ne 2 -or
    @($templateFiles | Where-Object Extension -eq '.csv').Count -ne 2 -or
    @($templateFiles | Where-Object Extension -eq '.txt').Count -ne 1 -or $templateFiles.Count -ne 5) {
    throw 'The templates folder must contain two XLSX files, two CSV files and one text guide.'
}
$thirdParty = Join-Path $repoRoot 'third-party-licenses'
if (-not (Test-Path -LiteralPath $thirdParty -PathType Container)) { throw 'Third-party license folder is missing.' }
$channel = if ($version -match '-preview\.') { 'preview' } else { 'release' }
$output = Join-Path $repoRoot ('artifacts\' + $channel + '\' + $version)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$zipPath = Join-Path $output "SmartTimeTableManager4-$version-win-x64.zip"
$templateZipPath = Join-Path $output "SmartTimeTableManager4-$version-Templates.zip"
$zipHashPath = Join-Path $output "SmartTimeTableManager4-$version-win-x64.sha256.txt"
$templateHashPath = Join-Path $output "SmartTimeTableManager4-$version-Templates.sha256.txt"
$sumsPath = Join-Path $output 'SHA256SUMS.txt'
foreach ($target in @($zipPath,$templateZipPath,$zipHashPath,$templateHashPath)) {
    if ((Test-Path -LiteralPath $target) -and -not $Force) {
        throw 'A release file already exists. Use build_release.cmd -Force to rebuild the same version.'
    }
}
$stage = Join-Path $output ('stage-' + [guid]::NewGuid().ToString('N'))
$appStage = Join-Path $stage 'app'
$templateStage = Join-Path $stage 'template-package'
$temporaryZip = Join-Path $stage 'app.zip'
$temporaryTemplateZip = Join-Path $stage 'templates.zip'
try {
    New-Item -ItemType Directory -Path $appStage -Force | Out-Null
    New-Item -ItemType Directory -Path $templateStage -Force | Out-Null
    # Standalone profile embeds runtime/native libraries. Never copy arbitrary publish-folder contents.
    if (@(Get-ChildItem -LiteralPath $PublishDirectory -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -like '*.runtimeconfig.json' }).Count -gt 0) {
        throw 'Use the single-file Standalone-win-x64 profile. This folder contains loose runtime files.'
    }
    Copy-Item -LiteralPath $exePath -Destination $appStage
    foreach ($document in $documents) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $document.Source) -Destination (Join-Path $appStage $document.Target) -Force
    }
    # Use source supplements, not stale copies from a previous publish folder.
    foreach ($folder in @('templates','third-party-licenses')) {
        $oldCopy = Join-Path $appStage $folder
        if (Test-Path -LiteralPath $oldCopy) { Remove-Item -LiteralPath $oldCopy -Recurse -Force }
        Copy-Item -LiteralPath (Join-Path $repoRoot $folder) -Destination $appStage -Recurse
    }
    Copy-Item -LiteralPath $templates -Destination $templateStage -Recurse
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($appStage,$temporaryZip,[IO.Compression.CompressionLevel]::Optimal,$false)
    [IO.Compression.ZipFile]::CreateFromDirectory($templateStage,$temporaryTemplateZip,[IO.Compression.CompressionLevel]::Optimal,$false)
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
        $required = @('TimeTableManager.exe') + @($documents | ForEach-Object { $_.Target }) +
            @($templateFiles | ForEach-Object { 'templates/' + $_.Name }) +
            @(Get-ChildItem -LiteralPath $thirdParty -File -Recurse | ForEach-Object {
                'third-party-licenses/' + $_.FullName.Substring($thirdParty.Length + 1).Replace('\','/')
            })
        $actualFiles = @($archive.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\','/') } | Sort-Object)
        if (@(Compare-Object @($required | Sort-Object) $actualFiles).Count -ne 0) { throw 'Runtime ZIP contains unexpected or missing files.' }
        foreach ($entry in $required) {
            if ($entries -notcontains $entry) { throw ('ZIP entry is missing: ' + $entry) }
        }
    }
    finally { $archive.Dispose() }
    $hash = (Get-FileHash -LiteralPath $temporaryZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $templateHash = (Get-FileHash -LiteralPath $temporaryTemplateZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $line = $hash + '  ' + [IO.Path]::GetFileName($zipPath)
    $templateLine = $templateHash + '  ' + [IO.Path]::GetFileName($templateZipPath)
    # UTF-8 without BOM, compatible with standard checksum tools.
    $encoding = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $stage 'app.sha256.txt'),$line + "`n",$encoding)
    [IO.File]::WriteAllText((Join-Path $stage 'templates.sha256.txt'),$templateLine + "`n",$encoding)
    [IO.File]::WriteAllText((Join-Path $stage 'SHA256SUMS.txt'),$line + "`n" + $templateLine + "`n",$encoding)
    Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force:$Force
    Move-Item -LiteralPath $temporaryTemplateZip -Destination $templateZipPath -Force:$Force
    Move-Item -LiteralPath (Join-Path $stage 'app.sha256.txt') -Destination $zipHashPath -Force:$Force
    Move-Item -LiteralPath (Join-Path $stage 'templates.sha256.txt') -Destination $templateHashPath -Force:$Force
    Move-Item -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Destination $sumsPath -Force
    Write-Output $zipPath
    Write-Output $templateZipPath
    Write-Output $zipHashPath
    Write-Output $templateHashPath
    Write-Output $sumsPath
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
