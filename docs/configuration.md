# Configuration

Phoenix is configured entirely through `appsettings.json`. Nothing about a company - its name,
its repository, its paths, its prerequisites - is compiled in.

## Where settings come from

Later sources win:

1. `appsettings.json`
2. `appsettings.{Environment}.json` (`Development`, `QA`, `Production`)
3. a file passed with `--config`
4. environment variables prefixed `PHOENIX_`
5. the command line

The environment is chosen by `--environment`, then `PHOENIX_ENVIRONMENT`, then
`DOTNET_ENVIRONMENT`, and defaults to `Production`.

Environment variables use `__` for nesting:

```powershell
$env:PHOENIX_Phoenix__Updates__Channel       = 'QA'
$env:PHOENIX_Phoenix__Directories__Root      = 'D:\Apps\Warehouse'
$env:PHOENIX_Phoenix__GitHub__ApiBaseUrl     = 'http://localhost:8099'
```

## Validation

Configuration is validated before any work starts. On failure Phoenix prints each problem and
exits with code 3:

```
Phoenix is not set up correctly on this computer.

Environment: Production

  - Phoenix:Application:Id is required.
  - Prerequisite 'python' has a non-HTTPS Installer:Url, which Security:RequireHttps forbids.

Fix appsettings.json (or the matching environment file) and run Phoenix again.
```

Discovering a typo at startup is much cheaper than discovering it halfway through replacing a
working installation.

## Adopting Phoenix for another company

These are the settings that actually change. Everything else has a sensible default.

| Setting | Meaning |
| --- | --- |
| `Company:Name`, `Company:SupportEmail`, `Company:SupportText` | Shown on the welcome screen and on failures |
| `Product:Name`, `Product:Slug` | The slug becomes the directory name |
| `Branding:ConsoleTitle`, `WelcomeText`, `AccentColor` | Console title bar, subtitle, accent colour (name or `#rrggbb`) |
| `GitHub:Owner`, `GitHub:Repository` | Where releases come from |
| `Application:Id` | Must equal `applicationId` in the manifest, or the release is refused |
| `Application:Url` | Where the application serves, and what the browser opens |
| `Directories:Root` | Everything Phoenix owns. `%VAR%` and `{ProductSlug}` are expanded |
| `Updates:Channel` | `Development`, `QA` or `Production` |
| `Prerequisites:Required` | What the application needs on the machine |

A complete example: [../config/examples/appsettings.contoso.json](../config/examples/appsettings.contoso.json).

## Reference

### Company, Product, Branding

| Key | Default | Notes |
| --- | --- | --- |
| `Company:Name` | *required* | |
| `Company:SupportEmail` / `SupportUrl` / `SupportText` | none | `SupportText` wins if set |
| `Product:Name` | *required* | |
| `Product:Slug` | derived from the name | Sanitised for the filesystem |
| `Branding:ConsoleTitle` | `Phoenix` | |
| `Branding:WelcomeText` | company + product | |
| `Branding:AccentColor` | `deepskyblue1` | Spectre colour name or hex |
| `Branding:GreetUserByName` | `true` | Windows display name, first name only |

### GitHub

| Key | Default | Notes |
| --- | --- | --- |
| `GitHub:Owner` / `Repository` | *required* | |
| `GitHub:ApiBaseUrl` | `https://api.github.com` | Change for GitHub Enterprise or local testing |
| `GitHub:TokenEnvironmentVariable` | `PHOENIX_GITHUB_TOKEN` | The *name* of the variable. The token itself is never stored in configuration and never logged |
| `GitHub:IncludeDrafts` | `false` | |
| `GitHub:MaxReleasesToInspect` | `30` | |

### Application

| Key | Default | Notes |
| --- | --- | --- |
| `Application:Id` | *required* | Cross-checked against the manifest |
| `Application:Name` | *required* | |
| `Application:Url` | `http://localhost:5080` | |
| `Application:AdditionalArguments` | `[]` | Appended to the manifest arguments |
| `Application:EnvironmentVariables` | `{}` | Merged over the manifest's |
| `Application:ShutdownTimeoutSeconds` | `20` | Then the process is terminated |
| `Application:StartupGraceSeconds` | `2` | An exit inside this window counts as a startup crash |
| `Application:ReuseRunningInstance` | `true` | Do not restart something already running |

### Updates

| Key | Default | Notes |
| --- | --- | --- |
| `Updates:Channel` | `Production` | |
| `Updates:Policy` | `Automatic` | `Automatic`, `CheckOnly`, `Never`, `Pinned` |
| `Updates:PinnedVersion` | none | Required when `Policy` is `Pinned` |
| `Updates:RemoteFallbackCount` | `2` | How many older releases may be tried |
| `Updates:AllowDowngrade` | `false` | |
| `Updates:ConfirmBeforeUpdate` | `false` | |

### Directories

| Key | Default |
| --- | --- |
| `Directories:Root` | `%LOCALAPPDATA%\Phoenix\{ProductSlug}` |
| `Directories:Versions` / `Staging` / `Backup` / `Cache` / `Logs` / `State` | subfolders of the root |

Use `%ProgramData%\...` for a machine-wide installation, and make sure users can write there.

### HealthCheck

| Key | Default | Notes |
| --- | --- | --- |
| `HealthCheck:Enabled` | `true` | Turning it off means versions become known good without proof. Not recommended |
| `HealthCheck:Path` | `/health` | The manifest may override it per release |
| `HealthCheck:Url` | none | Absolute URL; wins over `Path` |
| `HealthCheck:RequestTimeoutSeconds` | `5` | |
| `HealthCheck:RetryIntervalMilliseconds` | `1000` | |
| `HealthCheck:MaxAttempts` | `40` | |
| `HealthCheck:OverallTimeoutSeconds` | `90` | Whichever bound is hit first wins |
| `HealthCheck:ExpectedStatusCodes` | `[200]` | |

### Recovery

| Key | Default | Notes |
| --- | --- | --- |
| `Recovery:EnableAutomaticRollback` | `true` | |
| `Recovery:KeepVersions` | `3` | Active and known-good are never counted against this |
| `Recovery:BackupRetention` | `2` | |
| `Recovery:RequireHealthCheckForKnownGood` | `true` | |

### Network

| Key | Default |
| --- | --- |
| `Network:RequestTimeoutSeconds` | `30` |
| `Network:DownloadTimeoutMinutes` | `30` |
| `Network:MaxRetries` | `3` |
| `Network:RetryBaseDelayMilliseconds` | `500` |
| `Network:UserAgentSuffix` | none |

### Security

| Key | Default | Notes |
| --- | --- | --- |
| `Security:RequireHttps` | `true` | Loopback is exempt, for local testing |
| `Security:RequireChecksum` | `true` | |
| `Security:RequireSignature` | `false` | Authenticode on the executable and on installers |
| `Security:AllowedCertificateThumbprints` | `[]` | Empty means any valid chain |
| `Security:AllowedDownloadHosts` | GitHub hosts | Empty means any HTTPS host |
| `Security:MaxPackageSizeBytes` | 2 GiB | |
| `Security:MaxExtractedSizeBytes` | 8 GiB | Bounds a zip bomb |
| `Security:MaxArchiveEntries` | 200000 | |
| `Security:DiskSpaceMarginBytes` | 512 MiB | |

### Browser and Testing

| Key | Default | Notes |
| --- | --- | --- |
| `Browser:LaunchOnSuccess` | `true` | Only after a successful health check, and only once |
| `Browser:Url` | none | Overrides the application URL for the browser only |
| `Testing:SimulateFault` | `None` | Rejected outside `Development` |
| `Testing:SimulatePrerequisiteInstalls` | `false` | Rejected outside `Development` |
| `Testing:AllowBrowserLaunch` | `true` | |

`Testing:SimulateFault` accepts `DownloadFailure`, `InterruptedDownload`, `ChecksumMismatch`,
`CorruptArchive`, `StartupFailure`, `HealthCheckFailure` and `ActivationFailure`. Configuration
validation refuses any of them in QA or Production, so a production machine cannot be
configured to sabotage itself.

### Serilog

An optional `Serilog` section is read if present and can add sinks or raise levels without a
rebuild. Without it, Phoenix writes rolling daily files to `{Root}\logs` and only writes to the
console in diagnostic mode.
