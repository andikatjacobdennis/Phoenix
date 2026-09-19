<#
.SYNOPSIS
    Builds the release artifacts Phoenix consumes: the packages, their checksums and the
    release manifest that ties the two together.

.DESCRIPTION
    Produces, in the output directory:

      phoenix-win-x64.zip            Phoenix itself, self-contained (no runtime needed)
      phoenix-demoapp-win-x64.zip    The demo application, framework-dependent
      release-manifest.json          What Phoenix reads to decide what to install
      SHA256SUMS                     Checksums in the usual "hash *name" format

    The same script runs locally and in CI, so a release built on a laptop is byte-for-byte
    the same shape as one built by GitHub Actions.

.PARAMETER Version
    Semantic version. The prerelease label decides the channel:
    1.2.0 is Production, 1.2.0-qa.1 is QA, 1.2.0-dev.5 is Development.

.PARAMETER OutputDirectory
    Where the artifacts are written. Defaults to ./artifacts.

.PARAMETER Runtime
    Runtime identifier to publish for. Defaults to win-x64.

.PARAMETER SkipPhoenix
    Package only the demo application. Useful when testing application updates.

.EXAMPLE
    ./scripts/package.ps1 -Version 1.1.0

.EXAMPLE
    ./scripts/package.ps1 -Version 1.2.0-qa.3 -OutputDirectory ./artifacts/qa
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..' 'artifacts'),

    [string] $Runtime = 'win-x64',

    [string] $Configuration = 'Release',

    [switch] $SkipPhoenix
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

function Get-Channel([string] $version) {
    if ($version -notmatch '-') { return 'Production' }
    $label = ($version -split '-', 2)[1].Split('.')[0].ToLowerInvariant()
    switch ($label) {
        'qa'    { 'QA' }
        'beta'  { 'QA' }
        'rc'    { 'QA' }
        default { 'Development' }
    }
}

function Get-OperatingSystemMoniker([string] $runtime) {
    if ($runtime.StartsWith('win')) { return 'win' }
    if ($runtime.StartsWith('osx')) { return 'osx' }
    return 'linux'
}

function Get-Architecture([string] $runtime) {
    return ($runtime -split '-', 2)[1]
}

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "'$Version' is not a valid semantic version (expected MAJOR.MINOR.PATCH[-label.N])."
}

$channel = Get-Channel $Version
$os = Get-OperatingSystemMoniker $Runtime
$architecture = Get-Architecture $Runtime

Write-Host "Packaging version $Version ($channel) for $Runtime" -ForegroundColor Cyan

if (Test-Path $OutputDirectory) {
    Remove-Item $OutputDirectory -Recurse -Force
}

$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$staging = Join-Path $OutputDirectory '_publish'

function Publish-Project {
    param(
        [string] $ProjectPath,
        [string] $PublishDirectory,
        [bool] $SelfContained
    )

    $arguments = @(
        'publish', $ProjectPath,
        '--configuration', $Configuration,
        '--runtime', $Runtime,
        '--output', $PublishDirectory,
        "-p:PhoenixVersion=$Version",
        "-p:SelfContained=$($SelfContained.ToString().ToLowerInvariant())",
        '--nologo'
    )

    if (-not $SelfContained) {
        $arguments += '--no-self-contained'
    }

    Write-Host "  publishing $(Split-Path $ProjectPath -Leaf)" -ForegroundColor DarkGray
    & dotnet @arguments | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $ProjectPath (exit code $LASTEXITCODE)."
    }
}

function New-Package {
    param(
        [string] $PublishDirectory,
        [string] $ZipPath
    )

    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path (Join-Path $PublishDirectory '*') -DestinationPath $ZipPath -CompressionLevel Optimal
    return (Get-FileHash $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ---- Demo application (the thing Phoenix installs) ----------------------------

$demoPublish = Join-Path $staging 'demoapp'
Publish-Project -ProjectPath (Join-Path $repoRoot 'src/Phoenix.DemoApp/Phoenix.DemoApp.csproj') `
                -PublishDirectory $demoPublish -SelfContained $false

$demoPackageName = "phoenix-demoapp-$Runtime.zip"
$demoPackagePath = Join-Path $OutputDirectory $demoPackageName
$demoHash = New-Package -PublishDirectory $demoPublish -ZipPath $demoPackagePath
$demoSize = (Get-Item $demoPackagePath).Length

Write-Host "  $demoPackageName  $([math]::Round($demoSize / 1MB, 1)) MB" -ForegroundColor DarkGray

# ---- Phoenix itself -----------------------------------------------------------

$phoenixPackageName = $null

if (-not $SkipPhoenix) {
    $phoenixPublish = Join-Path $staging 'phoenix'
    Publish-Project -ProjectPath (Join-Path $repoRoot 'src/Phoenix/Phoenix.csproj') `
                    -PublishDirectory $phoenixPublish -SelfContained $true

    # Phoenix diagnoses itself through its Serilog files, so the symbols only add weight here.
    Get-ChildItem $phoenixPublish -Filter *.pdb -Recurse | Remove-Item -Force

    $phoenixPackageName = "phoenix-$Runtime.zip"
    $phoenixPackagePath = Join-Path $OutputDirectory $phoenixPackageName
    $phoenixHash = New-Package -PublishDirectory $phoenixPublish -ZipPath $phoenixPackagePath
    $phoenixSize = (Get-Item $phoenixPackagePath).Length

    Write-Host "  $phoenixPackageName  $([math]::Round($phoenixSize / 1MB, 1)) MB" -ForegroundColor DarkGray
}

# ---- Release manifest ---------------------------------------------------------

$manifest = [ordered]@{
    schemaVersion   = 1
    applicationId   = 'phoenix-demoapp'
    applicationName = 'Phoenix Demo Application'
    version         = $Version
    channel         = $channel
    packages        = @(
        [ordered]@{
            operatingSystem = $os
            architecture    = $architecture
            packageFile     = $demoPackageName
            packageFormat   = 'zip'
            sha256          = $demoHash
            sizeBytes       = $demoSize
            executable      = 'Phoenix.DemoApp.exe'
            arguments       = @('--urls', 'http://localhost:5080')
            healthEndpoint  = '/health'
            environmentVariables = [ordered]@{
                ASPNETCORE_ENVIRONMENT = 'Production'
            }
        }
    )
    prerequisites = @(
        [ordered]@{
            id             = 'aspnetcore-runtime'
            minimumVersion = '9.0.0'
        }
    )
    minimumPhoenixVersion = '0.1.0'
    releaseMetadata = [ordered]@{
        builtAt    = (Get-Date).ToUniversalTime().ToString('o')
        commit     = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { (git -C $repoRoot rev-parse HEAD 2>$null) }
        runtime    = $Runtime
        builtBy    = if ($env:GITHUB_RUN_ID) { "github-actions/$($env:GITHUB_RUN_ID)" } else { 'local' }
    }
}

$manifestPath = Join-Path $OutputDirectory 'release-manifest.json'

# UTF-8 without a byte order mark: a BOM is legal in a file but trips strict JSON readers.
[System.IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 8),
    (New-Object System.Text.UTF8Encoding $false))

# ---- Checksums ----------------------------------------------------------------

$checksumLines = @()
foreach ($file in Get-ChildItem $OutputDirectory -File | Where-Object { $_.Extension -eq '.zip' } | Sort-Object Name) {
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumLines += "$hash *$($file.Name)"
}

$checksumLines | Set-Content -Path (Join-Path $OutputDirectory 'SHA256SUMS') -Encoding ascii

Remove-Item $staging -Recurse -Force

# ---- Validation ---------------------------------------------------------------
# A release that does not describe itself correctly is worse than no release at all.

$written = Get-Content $manifestPath -Raw | ConvertFrom-Json
foreach ($package in $written.packages) {
    $packagePath = Join-Path $OutputDirectory $package.packageFile
    if (-not (Test-Path $packagePath)) {
        throw "Manifest names '$($package.packageFile)' but that file was not produced."
    }

    $actual = (Get-FileHash $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $package.sha256) {
        throw "Checksum mismatch for $($package.packageFile)."
    }
}

Write-Host ''
Write-Host "Artifacts in $OutputDirectory" -ForegroundColor Green
Get-ChildItem $OutputDirectory -File | ForEach-Object {
    Write-Host ("  {0,-34} {1,10:N0} bytes" -f $_.Name, $_.Length)
}
