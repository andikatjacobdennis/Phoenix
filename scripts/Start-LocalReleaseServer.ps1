<#
.SYNOPSIS
    Serves local release artifacts through a minimal GitHub-compatible API, so the whole
    Phoenix flow can be exercised on one machine without publishing anything.

.DESCRIPTION
    Point -ArtifactsRoot at a folder containing one subfolder per version:

        artifacts/
          1.0.0/   release-manifest.json, phoenix-demoapp-win-x64.zip, SHA256SUMS
          1.1.0/   ...

    Each folder is produced by scripts/package.ps1. The server exposes the one endpoint
    Phoenix uses, /repos/{owner}/{repo}/releases, plus the asset downloads, and reads the
    channel straight out of each manifest.

    Phoenix allows plain HTTP for loopback addresses only, which is exactly this case.

.EXAMPLE
    ./scripts/package.ps1 -Version 1.0.0 -OutputDirectory ./artifacts/1.0.0
    ./scripts/package.ps1 -Version 1.1.0 -OutputDirectory ./artifacts/1.1.0
    ./scripts/Start-LocalReleaseServer.ps1

    Then, in another terminal:
    $env:PHOENIX_Phoenix__GitHub__ApiBaseUrl = 'http://localhost:8099'
    dotnet run --project src/Phoenix -- --environment Development --diagnostics
#>
[CmdletBinding()]
param(
    [string] $ArtifactsRoot = (Join-Path $PSScriptRoot '..' 'artifacts'),
    [int] $Port = 8099,
    [string] $Owner = 'andikatjacobdennis',
    [string] $Repository = 'Phoenix'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path $ArtifactsRoot)) {
    throw "Artifacts root '$ArtifactsRoot' does not exist. Run scripts/package.ps1 first."
}

$ArtifactsRoot = (Resolve-Path $ArtifactsRoot).Path
$prefix = "http://localhost:$Port/"

function Get-Releases {
    $releases = @()

    foreach ($directory in Get-ChildItem $ArtifactsRoot -Directory | Sort-Object Name -Descending) {
        $manifestPath = Join-Path $directory.FullName 'release-manifest.json'
        if (-not (Test-Path $manifestPath)) {
            Write-Warning "Skipping '$($directory.Name)': no release-manifest.json."
            continue
        }

        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $version = $manifest.version

        $assets = @()
        foreach ($file in Get-ChildItem $directory.FullName -File) {
            $assets += [ordered]@{
                name                 = $file.Name
                size                 = $file.Length
                content_type         = if ($file.Extension -eq '.zip') { 'application/zip' } else { 'application/json' }
                browser_download_url = "http://localhost:$Port/assets/$($directory.Name)/$($file.Name)"
                url                  = "http://localhost:$Port/assets/$($directory.Name)/$($file.Name)"
            }
        }

        $releases += [ordered]@{
            tag_name     = "v$version"
            name         = "$($manifest.applicationName) $version"
            draft        = $false
            prerelease   = $version.Contains('-')
            published_at = (Get-Item $manifestPath).LastWriteTimeUtc.ToString('o')
            assets       = $assets
        }
    }

    return $releases
}

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add($prefix)

try {
    $listener.Start()
}
catch {
    throw "Could not listen on $prefix. Try another port with -Port. ($($_.Exception.Message))"
}

Write-Host "Local release server listening on $prefix" -ForegroundColor Green
Write-Host "  releases: ${prefix}repos/$Owner/$Repository/releases" -ForegroundColor DarkGray
Write-Host "  serving:  $ArtifactsRoot" -ForegroundColor DarkGray
Write-Host ''
Write-Host 'Point Phoenix at it with:' -ForegroundColor Cyan
Write-Host "  `$env:PHOENIX_Phoenix__GitHub__ApiBaseUrl = '$($prefix.TrimEnd('/'))'" -ForegroundColor Cyan
Write-Host ''
Write-Host 'Press Ctrl+C to stop.' -ForegroundColor DarkGray

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request = $context.Request
        $response = $context.Response
        $path = $request.Url.AbsolutePath

        try {
            if ($path -eq "/repos/$Owner/$Repository/releases") {
                # -InputObject, not the pipeline: piping a collection into ConvertTo-Json in
                # Windows PowerShell wraps it in a {"value": [...]} envelope.
                $releases = @(Get-Releases)
                $json = ConvertTo-Json -InputObject $releases -Depth 8
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
                $response.ContentType = 'application/json'
                $response.StatusCode = 200
                $response.OutputStream.Write($bytes, 0, $bytes.Length)
            }
            elseif ($path -like '/assets/*') {
                # Resolve inside the artifacts root and refuse anything that escapes it.
                $relative = $path.Substring('/assets/'.Length)
                $candidate = Join-Path $ArtifactsRoot $relative
                $full = [System.IO.Path]::GetFullPath($candidate)

                if (-not $full.StartsWith($ArtifactsRoot, [StringComparison]::OrdinalIgnoreCase) -or
                    -not (Test-Path $full -PathType Leaf)) {
                    $response.StatusCode = 404
                }
                else {
                    $bytes = [System.IO.File]::ReadAllBytes($full)
                    $response.ContentType = 'application/octet-stream'
                    $response.ContentLength64 = $bytes.Length
                    $response.StatusCode = 200
                    $response.OutputStream.Write($bytes, 0, $bytes.Length)
                }
            }
            else {
                $response.StatusCode = 404
            }

            Write-Host ("  {0} {1} -> {2}" -f $request.HttpMethod, $path, $response.StatusCode) -ForegroundColor DarkGray
        }
        catch {
            Write-Warning "Request for $path failed: $($_.Exception.Message)"
            $response.StatusCode = 500
        }
        finally {
            $response.OutputStream.Close()
        }
    }
}
finally {
    $listener.Stop()
    $listener.Close()
    Write-Host 'Local release server stopped.' -ForegroundColor Yellow
}
