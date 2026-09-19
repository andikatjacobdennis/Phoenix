<#
.SYNOPSIS
    Checks that a set of release artifacts is internally consistent, before anybody publishes
    or installs them.

.DESCRIPTION
    Verifies, independently of the script that produced them:

      - the manifest parses and declares the expected version and channel;
      - every package named by the manifest exists;
      - every SHA-256 in the manifest matches the file on disk;
      - SHA256SUMS agrees with the manifest;
      - the executable the manifest names is actually inside the package;
      - the package contains no path-traversal entries.

    Exits non-zero on the first problem, which is what stops a release pipeline.

.EXAMPLE
    ./scripts/Test-ReleaseArtifacts.ps1 -ArtifactDirectory ./artifacts/1.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactDirectory,

    [string] $ExpectedVersion,

    [string] $ExpectedChannel
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path $ArtifactDirectory)) {
    throw "Artifact directory '$ArtifactDirectory' does not exist."
}

$ArtifactDirectory = (Resolve-Path $ArtifactDirectory).Path
$checks = 0

function Assert-True([bool] $condition, [string] $message) {
    $script:checks++
    if (-not $condition) {
        throw "FAILED: $message"
    }

    Write-Host "  ok  $message" -ForegroundColor DarkGray
}

Write-Host "Validating $ArtifactDirectory" -ForegroundColor Cyan

# ---- Manifest ----------------------------------------------------------------

$manifestPath = Join-Path $ArtifactDirectory 'release-manifest.json'
Assert-True (Test-Path $manifestPath) 'release-manifest.json is present'

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

Assert-True ($manifest.schemaVersion -eq 1) 'manifest schema version is 1'
Assert-True (-not [string]::IsNullOrWhiteSpace($manifest.applicationId)) 'manifest declares an application id'
Assert-True ($manifest.packages.Count -ge 1) 'manifest declares at least one package'

if ($ExpectedVersion) {
    Assert-True ($manifest.version -eq $ExpectedVersion) "manifest version is $ExpectedVersion"
}

if ($ExpectedChannel) {
    Assert-True ($manifest.channel -eq $ExpectedChannel) "manifest channel is $ExpectedChannel"
}

# The channel must agree with the version, exactly as Phoenix re-checks at install time.
$derivedChannel = if ($manifest.version -notmatch '-') {
    'Production'
} else {
    switch (($manifest.version -split '-', 2)[1].Split('.')[0].ToLowerInvariant()) {
        'qa'    { 'QA' }
        'beta'  { 'QA' }
        'rc'    { 'QA' }
        default { 'Development' }
    }
}

Assert-True ($manifest.channel -eq $derivedChannel) `
    "manifest channel '$($manifest.channel)' matches version '$($manifest.version)'"

# ---- Packages ----------------------------------------------------------------

foreach ($package in $manifest.packages) {
    $packagePath = Join-Path $ArtifactDirectory $package.packageFile

    Assert-True (Test-Path $packagePath) "$($package.packageFile) exists"

    $actualHash = (Get-FileHash $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True ($actualHash -eq $package.sha256.ToLowerInvariant()) `
        "$($package.packageFile) matches the SHA-256 in the manifest"

    Assert-True ((Get-Item $packagePath).Length -eq $package.sizeBytes) `
        "$($package.packageFile) matches the size in the manifest"

    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entries = $archive.Entries | ForEach-Object { $_.FullName }

        $expected = $package.executable.Replace('\', '/')
        Assert-True ($entries -contains $expected) `
            "$($package.packageFile) contains $($package.executable)"

        $dangerous = $entries | Where-Object {
            $_ -match '(^|/)\.\.(/|$)' -or $_ -match '^[a-zA-Z]:' -or $_.StartsWith('/')
        }

        Assert-True ($null -eq $dangerous -or @($dangerous).Count -eq 0) `
            "$($package.packageFile) contains no path-traversal entries"
    }
    finally {
        $archive.Dispose()
    }
}

# ---- Checksums file ----------------------------------------------------------

$sumsPath = Join-Path $ArtifactDirectory 'SHA256SUMS'
Assert-True (Test-Path $sumsPath) 'SHA256SUMS is present'

foreach ($line in Get-Content $sumsPath) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    $parts = $line -split '\s+\*?', 2
    $hash = $parts[0]
    $name = $parts[1]

    $path = Join-Path $ArtifactDirectory $name
    Assert-True (Test-Path $path) "SHA256SUMS references an existing file: $name"

    $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True ($actual -eq $hash.ToLowerInvariant()) "SHA256SUMS entry for $name is correct"
}

Write-Host ''
Write-Host "$checks checks passed." -ForegroundColor Green
