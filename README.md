# Phoenix

Phoenix is an enterprise bootstrapper. It installs, updates, repairs and launches a company's
ASP.NET Core application, and it is very hard to leave in a broken state.

A user double-clicks one executable. Phoenix checks the machine, installs whatever frameworks
are missing, works out which release this machine should be running, downloads and verifies it,
installs it without touching the working copy, starts the application, proves it is healthy,
and opens the browser. If any of that fails, the previous working version comes back.

```
Phoenix

Welcome, Sarah.

Checking your system                                                          ✓

Required components
.NET Runtime                                                            ✓ Ready

A new version is available.

Current                                                                   1.4.0
New                                                                       1.5.0

Downloading ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ 100%
Installing                                                                    ✓
Starting                                                                      ✓
Checking the application                                                      ✓

✓ Updated successfully

Example Application is ready.
Opening your browser...
```

Phoenix is not the business application. The application in this repository,
`Phoenix.DemoApp`, exists to prove the whole cycle end to end: install, update, break, recover.

## What it does

| | |
| --- | --- |
| **Prerequisites** | Detects .NET runtimes, Python, registry entries, file versions or any command's output, and installs what is missing - after verifying the installer's SHA-256. |
| **Releases** | Reads GitHub Releases, honours Development / QA / Production channels, and never installs a release from the wrong one. |
| **Safety** | HTTPS only, host allow-list, SHA-256 on every package, optional Authenticode, archive path-traversal protection, disk space checks. |
| **Transactional install** | Downloads to staging, verifies, extracts, validates, and only then makes the new version active - with one atomic state write. |
| **Recovery** | A version is known good only after it starts *and* passes a health check. Anything else rolls back, with a second backup copy as a fallback. |
| **Experience** | Spectre.Console for the person at the keyboard, Serilog for the person reading the logs afterwards. |

## Requirements

- Windows 10 or 11 (Phoenix targets `net9.0-windows`; it reads the registry and drives Windows installers)
- .NET SDK 9.0 or newer, to build
- No runtime at all, to *run* a published Phoenix: it ships self-contained

## Build and test

```bash
dotnet restore
dotnet build
dotnet test
```

228 tests: 211 unit tests, and 17 end-to-end tests that really download, install, start and
roll back a real ASP.NET Core application inside temporary directories.

## Run it

```bash
dotnet run --project src/Phoenix -- --diagnostics
```

Useful switches:

| Switch | Effect |
| --- | --- |
| `-d`, `--diagnostics` | Show paths, versions, release selection and health attempts |
| `--check-only` | Report whether an update exists, then launch what is installed |
| `--force-reinstall` | Reinstall the selected release even if it is already present |
| `--rollback` | Restore and launch the last known-good version |
| `--no-browser` | Do not open a browser |
| `--no-launch` | Install only |
| `-e`, `--environment` | `Development`, `QA` or `Production` (default `Production`) |
| `-c`, `--config` | Load an extra JSON configuration file |

Exit codes: `0` success, `1` unexpected, `2` cancelled, `3` configuration, `4` prerequisite,
`5` installation failed (previous version intact), `6` installation *and* recovery failed,
`7` another instance is running, `8` restart required.

## Try the whole cycle locally

No GitHub account needed. `scripts/Start-LocalReleaseServer.ps1` serves packaged artifacts
through a GitHub-shaped API on `localhost`.

```powershell
./scripts/package.ps1 -Version 1.0.0 -OutputDirectory ./artifacts/1.0.0
./scripts/Start-LocalReleaseServer.ps1
```

Then, in a second terminal:

```powershell
$env:PHOENIX_Phoenix__GitHub__ApiBaseUrl = 'http://localhost:8099'
$env:PHOENIX_Phoenix__Directories__Root  = "$env:TEMP\phoenix-local"
dotnet run --project src/Phoenix -- --diagnostics
```

Publish a second version and run Phoenix again to watch it update:

```powershell
./scripts/package.ps1 -Version 1.1.0 -OutputDirectory ./artifacts/1.1.0 -SkipPhoenix
```

[docs/testing.md](docs/testing.md) has the broken-release and rollback scenarios.

## Run the demo application on its own

```bash
dotnet run --project src/Phoenix.DemoApp -- --urls http://localhost:5080
```

`/` shows the company, version and environment; `/health` is what Phoenix polls; `/api/info`
returns the same details as JSON. The version comes from build metadata, so a new release
visibly changes the page after Phoenix updates it.

## Configuration

Everything company-specific lives in `src/Phoenix/appsettings.json` and its per-environment
overrides. Phoenix validates it at startup and refuses to run on a bad configuration rather
than failing halfway through an update.

The settings a company actually changes: `Company`, `Product`, `Branding`, `GitHub.Owner` and
`GitHub.Repository`, `Application.Id`, `Application.Url`, `Directories.Root`, `Updates.Channel`
and `Prerequisites.Required`. A fully worked example is in
[config/examples/appsettings.contoso.json](config/examples/appsettings.contoso.json).

Any setting can also be supplied as an environment variable:

```powershell
$env:PHOENIX_Phoenix__Updates__Channel = 'QA'
```

See [docs/configuration.md](docs/configuration.md).

## Creating a release

Push a tag; the channel comes from the version itself.

```bash
git tag v1.1.0        # Production
git tag v1.2.0-qa.1   # QA
git tag v1.3.0-dev.4  # Development
git push origin v1.1.0
```

`.github/workflows/release.yml` builds, tests, packages, validates and publishes
`phoenix-win-x64.zip`, `phoenix-demoapp-win-x64.zip`, `release-manifest.json` and `SHA256SUMS`.
See [docs/releases.md](docs/releases.md).

## Logs

`{Directories.Root}\logs\phoenix-<date>.log`, which defaults to
`%LOCALAPPDATA%\Phoenix\<ProductSlug>\logs`. Rolling daily, 14 days retained. Each entry
carries the operation id that the user is shown as `Reference: PX-7F2A91`, so a support ticket
maps to exactly one run. The managed application's own output goes to
`logs\application-<version>.log`.

## Documentation

| | |
| --- | --- |
| [architecture.md](docs/architecture.md) | Projects, the update state machine, the deployment model |
| [configuration.md](docs/configuration.md) | Every setting, and how to adopt Phoenix for another company |
| [releases.md](docs/releases.md) | Channels, the release manifest, publishing |
| [prerequisites.md](docs/prerequisites.md) | Detection strategies, installers, elevation, reboots |
| [recovery.md](docs/recovery.md) | Known-good tracking, rollback, state repair |
| [security.md](docs/security.md) | Threat model and the controls for each threat |
| [development.md](docs/development.md) | Working on Phoenix itself |
| [testing.md](docs/testing.md) | The test suites, and how to drive each failure scenario |

## Troubleshooting

| Symptom | Where to look |
| --- | --- |
| "Phoenix is not set up correctly" | The listed settings are wrong or missing; exit code 3 |
| "Phoenix is already running" | Another instance holds the lock; exit code 7 |
| "We could not check for updates" | Network or GitHub; the installed version is launched anyway |
| "The update was not applied..." | The new release failed; the previous one was restored. The log has the reason under that reference |
| Nothing installs, exit code 5 | Usually a channel mismatch: a Production machine ignores QA and Development releases by design |
| Stuck on "restart required" | A prerequisite installer asked for a reboot; restart and run Phoenix again |

## Licence

MIT. See [LICENSE](LICENSE).
