param([switch]$Force, [switch]$ValidateOnly)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PublicSourceFiles.json') -Encoding UTF8 -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.root_name -ne 'SmartTimeTableManager4') { throw 'Unsupported public source manifest.' }
$files = @($manifest.files | Sort-Object)
if ($files.Count -eq 0 -or @($files | Select-Object -Unique).Count -ne $files.Count) { throw 'Empty or duplicate public source entries.' }
$privateNames = @('AGENTS.md','GITHUB_SETUP.txt','BUILD_RELEASE_GUIDE.txt','VISUAL_STUDIO_GUIDE.txt','RELEASE_NOTES.md','SOURCE_SHA256.txt')
foreach ($relative in $files) {
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('\') -or $relative -match '(^|/)\.\.(/|$)|:') {
        throw ('Unsafe manifest path: ' + $relative)
    }
    if ($relative -match '(^|/)(private|web|docs|bin|obj|artifacts|\.git|\.vs)(/|$)' -or
        $privateNames -contains $relative -or $relative -match '\.(ttm|log|exe|dll|pdb|user|suo)$') {
        throw ('Private or generated file is not public source: ' + $relative)
    }
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relative) -PathType Leaf)) {
        throw ('Required public source file is missing: ' + $relative)
    }
}
# Default compile inputs must not silently disappear from the public source ZIP.
foreach ($folder in @('src','tests')) {
    foreach ($item in @(Get-ChildItem -LiteralPath (Join-Path $repoRoot $folder) -Recurse -File)) {
        $relative = $item.FullName.Substring($repoRoot.Length + 1).Replace('\','/')
        if ($relative -match '/(bin|obj)/') { continue }
        if ($item.Extension -in @('.cs','.xaml','.csproj','.resx') -and $files -notcontains $relative) {
            throw ('Review this new build input and add it to PublicSourceFiles.json: ' + $relative)
        }
    }
}
if ($ValidateOnly) { Write-Output 'Public source file list validated.'; return }
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Encoding UTF8 -Raw
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Set a stable version before exporting.' }
$output = Join-Path $repoRoot 'artifacts\public'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$zipPath = Join-Path $output "SmartTimeTableManager4-$version-GitHub.zip"
$hashPath = Join-Path $output "SmartTimeTableManager4-$version-GitHub.sha256.txt"
if (-not $Force -and ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $hashPath))) {
    throw 'Public source ZIP already exists. Use -Force to export again.'
}
# Git uses the same explicit file list. Unknown files stay ignored even under src/tools.
$directories = @($files | ForEach-Object {
    $parts = $_.Split('/')
    for ($i = 1; $i -lt $parts.Length; $i++) { ($parts[0..($i-1)] -join '/') }
} | Sort-Object -Unique)
$ignoreLines = @('# Generated from tools/PublicSourceFiles.json.', '# Only reviewed public source files may be tracked.', '*', '')
$ignoreLines += @($directories | ForEach-Object { '!/' + $_ + '/' })
$ignoreLines += @($files | ForEach-Object { '!/' + $_ })
$encoding = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $repoRoot '.gitignore'),($ignoreLines -join "`n") + "`n",$encoding)
$stage = Join-Path $output ('source-stage-' + [guid]::NewGuid().ToString('N'))
$sourceStage = Join-Path $stage $manifest.root_name
$temporaryZip = Join-Path $stage 'source.zip'
try {
    New-Item -ItemType Directory -Path $sourceStage -Force | Out-Null
    $sourceHashes = @()
    foreach ($relative in $files) {
        $source = Join-Path $repoRoot $relative
        $destination = Join-Path $sourceStage $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
        $sourceHashes += (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
    }
    [IO.File]::WriteAllText((Join-Path $sourceStage 'SOURCE_SHA256.txt'),($sourceHashes -join "`n") + "`n",$encoding)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Include the repository root without adding stage paths or other files.
    $packagedFiles = @($files) + @('SOURCE_SHA256.txt')
    $archive = [IO.Compression.ZipFile]::Open($temporaryZip,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in $packagedFiles) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $sourceStage $relative),$manifest.root_name + '/' + $relative,[IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
    try {
        $actual = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
        $expected = @($packagedFiles | ForEach-Object { $manifest.root_name + '/' + $_ } | Sort-Object)
        if (@(Compare-Object $expected $actual).Count -ne 0) { throw 'Public ZIP differs from the approved file list.' }
    }
    finally { $archive.Dispose() }
    $hash = (Get-FileHash -LiteralPath $temporaryZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $line = $hash + '  ' + [IO.Path]::GetFileName($zipPath)
    Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force:$Force
    [IO.File]::WriteAllText($hashPath,$line + "`n",$encoding)
    $sumsPath = Join-Path $output 'SHA256SUMS.txt'
    $sums = @()
    if (Test-Path -LiteralPath $sumsPath) {
        $suffix = '  ' + [IO.Path]::GetFileName($zipPath)
        $sums = @(Get-Content -LiteralPath $sumsPath -Encoding UTF8 | Where-Object { $_ -and -not $_.EndsWith($suffix) })
    }
    [IO.File]::WriteAllText($sumsPath,((@($sums) + @($line)) -join "`n") + "`n",$encoding)
    Write-Output $zipPath
    Write-Output $hashPath
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
