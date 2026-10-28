[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if ($SkipTests) {
        dotnet build 'src/Jellyfin.Plugin.OutroCompletion/Jellyfin.Plugin.OutroCompletion.csproj' --configuration Release
    } else {
        dotnet test 'Jellyfin.Plugin.OutroCompletion.slnx' --configuration Release --logger 'trx;LogFileName=outro-completion.trx' --results-directory 'artifacts/test-results'
    }
    if ($LASTEXITCODE -ne 0) { throw 'Build or tests failed. No package was created.' }

    $assembly = Join-Path $PSScriptRoot 'src/Jellyfin.Plugin.OutroCompletion/bin/Release/net10.0/Jellyfin.Plugin.OutroCompletion.dll'
    $version = [System.Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString()
    $artifacts = Join-Path $PSScriptRoot 'artifacts'
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    $zip = Join-Path $artifacts "OutroCompletion_$version.zip"
    # Do NOT ship Jellyfin or ASP.NET assemblies: the server owns those dependencies.
    Compress-Archive -LiteralPath @($assembly, (Join-Path $PSScriptRoot 'README.md'), (Join-Path $PSScriptRoot 'LICENSE')) -DestinationPath $zip -Force
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    # Jellyfin's repository installer specifically requires MD5, not SHA-256.
    $md5 = (Get-FileHash -LiteralPath $zip -Algorithm MD5).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath "$zip.sha256" -Encoding utf8NoBOM
    "$md5  $([System.IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath "$zip.md5" -Encoding utf8NoBOM
    Write-Output "Package: $zip"
    Write-Output "SHA256:  $hash"
    Write-Output "MD5:     $md5"
} finally {
    Pop-Location
}
