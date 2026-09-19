# Testing

```bash
dotnet test                                                   # everything
dotnet test tests/Phoenix.Tests                               # unit, ~1s
dotnet test tests/Phoenix.IntegrationTests                    # end to end, ~55s
dotnet test --filter "FullyQualifiedName~rolled_back"         # one scenario
```

Both suites run on every push and pull request. Everything happens inside temporary
directories; no test installs machine-wide software or touches a real installation.

## Unit tests (211)

| Area | What is covered |
| --- | --- |
| `SemanticVersion` | Parsing, the full specification ordering, prerelease precedence, build metadata, malformed input |
| `VersionRange` | Interval notation, minimum/maximum/exact, inverted ranges |
| `ChannelResolver` | Tag to channel, no inheritance between channels, tag versus prerelease-flag consistency |
| `ReleaseSelector` | Channel filtering, ordering, bounded fallback, drafts, missing manifests, rejected versions, pinning, and every update decision |
| `ManifestValidator` | Wrong application, wrong channel, wrong version, unsafe executable, unsafe package name, bad checksum, missing package, Phoenix too old |
| `PhoenixOptionsValidator` | Missing fields, bad URLs, duplicate prerequisite ids, per-strategy requirements, fault injection outside Development |
| `PhoenixPaths` | Variable expansion, slug sanitising, derived layout |
| `ZipPackageExtractor` | Path traversal in six shapes, entry and size caps, corrupt archives |
| `PackageVerifier` | Matching and mismatching hashes, missing files, required-checksum enforcement, a known SHA-256 |
| `JsonStateStore` | Round trip, corrupt file, backup fallback, atomic update, bounded rejection list |
| `InstallationManager` | Activation, known-good marking and backup, rollback, restore from backup, state repair, stale state |
| `CleanupService` | Never removes active or known-good, removes abandoned staging, expired cache, partial downloads |
| `DownloadManager` | HTTPS enforcement, host allow-list, retry of transient failures only, size caps, no partial files left |
| `HealthCheckService` | Success, attempt budget, early exit when the process dies, cancellation |
| `GitHubReleaseProvider` | Parsing, drafts, rate limiting, auth errors, retries, malformed JSON, token handling |
| `TransientFailure` | Which failures are worth retrying, bounded backoff |
| Detection | Version extraction from real tool output, argument splitting, .NET runtime detection, PATH resolution |

Network, process and filesystem boundaries are faked with small hand-written doubles rather
than a mocking framework - the fakes are short, and they double as documentation of what
Phoenix expects from each boundary.

## Integration tests (17)

These are the ones that matter. A `FakeGitHubServer` (real Kestrel, real HTTP) serves releases
built from the real demo application's build output. Phoenix downloads over HTTP, verifies real
checksums, extracts real archives, starts a real ASP.NET Core process and polls its real health
endpoint.

| Scenario | Asserted |
| --- | --- |
| First installation | Installed, known good, backed up, health endpoint answers, prerequisite detected |
| Update to a newer release | New version active and known good, previous version retained |
| Release never becomes healthy | Rolled back, previous version running, version rejected, user told |
| Release crashes on startup | Same, detected inside the startup grace period |
| Corrupt checksum on the newest release | Refused, older *release* installed instead, bad version never unpacked |
| Malicious archive | Refused, nothing written outside staging, nothing installed |
| Release for another application | Refused |
| Production machine, QA release only | Nothing installed |
| QA machine, QA release | Installed |
| Release source unreachable | Installed version launched anyway |
| Nothing installed and no release source | Clean failure, message contains no URL or exception text |
| Already up to date | No download at all, no update announcement |
| Explicit `--rollback` | Known-good version restored and serving |
| Second Phoenix instance | Refused with exit code 7, nothing installed |
| `--check-only` with an update available | Reports it, installs nothing |
| Cancellation | Exit code 2, nothing half-installed |
| Corrupt state file and backup | State rebuilt from disk, application back up |

The demo application cooperates through one environment variable, `PHOENIX_DEMO_SIMULATE`,
which the manifest can set to `crash` or `unhealthy`. The failure Phoenix recovers from is a
real process really failing, not a stubbed verdict.

## Driving the failure scenarios by hand

`Testing:SimulateFault` (Development only) injects a fault into Phoenix's own pipeline:

| Value | Effect |
| --- | --- |
| `DownloadFailure` | The download throws |
| `InterruptedDownload` | The connection drops halfway |
| `ChecksumMismatch` | Verification fails on an intact file |
| `CorruptArchive` | Extraction refuses the archive |
| `StartupFailure` | The application is started in crash mode |
| `HealthCheckFailure` | The application is started in unhealthy mode |
| `ActivationFailure` | Activation fails after the candidate is prepared |

```powershell
$env:PHOENIX_Phoenix__Testing__SimulateFault = 'HealthCheckFailure'
dotnet run --project src/Phoenix -- --environment Development --diagnostics
```

Configuration validation refuses any of these outside Development, so this cannot be turned on
in production by accident.

## A full local walkthrough

```powershell
# 1. Two releases
./scripts/package.ps1 -Version 1.0.0 -OutputDirectory ./artifacts/1.0.0
./scripts/package.ps1 -Version 1.1.0 -OutputDirectory ./artifacts/1.1.0 -SkipPhoenix

# 2. Serve them
./scripts/Start-LocalReleaseServer.ps1

# 3. In another terminal
$env:PHOENIX_Phoenix__GitHub__ApiBaseUrl = 'http://localhost:8099'
$env:PHOENIX_Phoenix__Directories__Root  = "$env:TEMP\phoenix-local"

# First installation: 1.1.0 is newest, so that is what lands
dotnet run --project src/Phoenix -- --diagnostics

# Update: remove 1.1.0, install 1.0.0, put 1.1.0 back, run again
```

To watch a rollback, edit `artifacts/1.1.0/release-manifest.json` and add
`"PHOENIX_DEMO_SIMULATE": "unhealthy"` to the package's `environmentVariables` - then recompute
nothing, because the manifest hash covers the package, not the manifest. Run Phoenix again:
1.1.0 installs, starts, never becomes healthy, and 1.0.0 comes back.

## Validating release artifacts

```powershell
./scripts/Test-ReleaseArtifacts.ps1 -ArtifactDirectory ./artifacts/1.1.0 `
                                    -ExpectedVersion 1.1.0 -ExpectedChannel Production
```

Fifteen independent checks: the manifest parses, its channel agrees with its version, every
package exists and matches its hash and size, the executable really is inside the zip, no entry
tries to traverse, and `SHA256SUMS` agrees with all of it. CI runs this after packaging, and
nothing is published if it fails.

## Notes

- Integration tests run serially. They bind ports and kill processes; parallelism would only
  make failures confusing.
- If a run is interrupted, a demo application process may survive. `Get-Process
  Phoenix.DemoApp | Stop-Process` clears it.
- Piping Phoenix's output into another command blocks until the launched application exits:
  the child inherits the pipe. Redirect to a file instead when scripting.
