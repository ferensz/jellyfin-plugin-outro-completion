[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$Tag,
    [string]$Changelog,
    [DateTimeOffset]$Timestamp = [DateTimeOffset]::UtcNow,
    [string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/manifest.json'),
    [string]$ExistingManifestPath = (Join-Path $PSScriptRoot '../manifest.json')
)

$ErrorActionPreference = 'Stop'
$repository = 'ferensz/jellyfin-plugin-outro-completion'
$pluginId = '0c220e70-bcdf-4af5-83a3-36f656694392'
$projectPath = Join-Path $PSScriptRoot '../src/Jellyfin.Plugin.OutroCompletion/Jellyfin.Plugin.OutroCompletion.csproj'
$project = [xml](Get-Content -LiteralPath $projectPath -Raw)
$version = [Version]$project.Project.PropertyGroup.AssemblyVersion
$targetAbi = [string]$project.Project.PropertyGroup.JellyfinTargetAbi
if (-not $Tag) { $Tag = "v$($version.ToString(3))" }
if ($Tag -notmatch '^v\d+\.\d+\.\d+(\.\d+)?$') { throw 'The release tag must be vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH.REVISION.' }
$tagVersion = [Version]$Tag.Substring(1)
$normalizedTag = [Version]::new($tagVersion.Major, $tagVersion.Minor, $tagVersion.Build, [Math]::Max(0, $tagVersion.Revision))
if ($normalizedTag -ne $version) { throw "Tag $Tag does not match assembly version $version." }
if (-not $Changelog) { $Changelog = "See https://github.com/$repository/releases/tag/$Tag for release notes." }

$package = Get-Item -LiteralPath $PackagePath
$expectedName = "OutroCompletion_$version.zip"
if ($package.Name -cne $expectedName) { throw "Expected a package named $expectedName." }
$assemblyPath = Join-Path $PSScriptRoot '../src/Jellyfin.Plugin.OutroCompletion/bin/Release/net10.0/Jellyfin.Plugin.OutroCompletion.dll'
if ([System.Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version -ne $version) { throw 'Build the current plugin version before generating its manifest.' }

# Guard against accidentally advertising an older ZIP alongside a newer build.
$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entry = $archive.GetEntry('Jellyfin.Plugin.OutroCompletion.dll')
    if ($null -eq $entry) { throw 'The plugin DLL must be at the root of the ZIP.' }
    $stream = $entry.Open()
    try { $packagedHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
    if ($packagedHash -ne (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash) { throw 'The packaged DLL does not match the current build.' }
} finally {
    $archive.Dispose()
}

$existingText = Get-Content -LiteralPath $ExistingManifestPath -Raw
if (-not $existingText.TrimStart().StartsWith('[')) { throw 'The existing manifest must be a JSON array.' }
$manifest = @($existingText | ConvertFrom-Json -AsHashtable)
if ($manifest.Count -ne 1 -or $manifest[0].guid -ne $pluginId) { throw 'The existing manifest must describe only Outro Completion.' }
$plugin = $manifest[0]
$md5 = (Get-FileHash -LiteralPath $package.FullName -Algorithm MD5).Hash.ToLowerInvariant()
$sourceUrl = "https://github.com/$repository/releases/download/$Tag/$expectedName"
$oldVersion = @($plugin.versions | Where-Object { $_.version -eq $version.ToString() })
if ($oldVersion.Count -gt 0) {
    if ($oldVersion.Count -ne 1 -or $oldVersion[0].checksum -ne $md5 -or $oldVersion[0].sourceUrl -ne $sourceUrl -or $oldVersion[0].targetAbi -ne $targetAbi) {
        throw 'This version is already advertised with different release data. Publish a new version instead of replacing an existing release.'
    }
} else {
    $release = [ordered]@{
        version = $version.ToString()
        changelog = $Changelog
        targetAbi = $targetAbi
        sourceUrl = $sourceUrl
        checksum = $md5
        timestamp = $Timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    }
    $plugin.versions = @(@($plugin.versions) + @($release) | Sort-Object { [Version]$_.version } -Descending)
}

$parent = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $parent -Force | Out-Null
ConvertTo-Json -InputObject @($plugin) -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output "Manifest: $([System.IO.Path]::GetFullPath($OutputPath))"
Write-Output "Release:  $sourceUrl"
Write-Output "MD5:      $md5"
