# Prerequisites

Phoenix installs the frameworks the application needs - .NET runtimes, Python, Visual C++
redistributables, WebView2, Node, anything with a silent installer - and it does it in the same
order every time:

```
detect → compatible? → yes: leave it alone
                     → no:  download → verify SHA-256 → install → detect again → confirm
```

Nothing is ever installed blindly, and a newer compatible version is never downgraded.

## Defining one

```json
{
  "Id": "python",
  "DisplayName": "Python",
  "Mandatory": true,
  "MinimumVersion": "3.11.0",
  "Architecture": "any",
  "ApplicableEnvironments": [],
  "Detection": {
    "Strategy": "ExecutableVersion",
    "Path": "python.exe",
    "Arguments": "--version",
    "VersionPattern": "Python (?<version>\\d+\\.\\d+\\.\\d+)"
  },
  "Installer": {
    "Url": "https://www.python.org/ftp/python/3.12.4/python-3.12.4-amd64.exe",
    "Sha256": "…64 hex characters…",
    "SilentArguments": "/quiet InstallAllUsers=0 PrependPath=1",
    "AcceptedExitCodes": [0],
    "RebootExitCodes": [3010, 1641]
  },
  "RequiresAdministrator": false,
  "RequiresRestart": false,
  "TimeoutSeconds": 900
}
```

| Field | Notes |
| --- | --- |
| `Id` | Unique; the manifest refers to it |
| `Mandatory` | Mandatory failures stop the run; optional ones warn |
| `RequiredVersion` / `MinimumVersion` / `MaximumVersion` / `VersionRange` | `VersionRange` wins. Interval notation: `[9.0,11.0)`, `(1.0,2.0]`, `[3.12.4]` |
| `Architecture` | `x64`, `x86`, `arm64` or `any` |
| `ApplicableEnvironments` | Empty means every environment |
| `RequiresAdministrator` | Elevation is requested for *this installer*, not for Phoenix |
| `RequiresRestart` | Treat a successful install as needing a reboot anyway |

## Detection strategies

| Strategy | How it works | Needs |
| --- | --- | --- |
| `DotNetRuntime` | Enumerates `shared/{RuntimeName}` under every .NET root, then falls back to `dotnet --list-runtimes` | `RuntimeName` |
| `ExecutableVersion` | Runs the program and reads a version out of its output | `Path`, optional `Arguments`, optional `VersionPattern` |
| `FileVersion` | Reads a file's version resource | `Path` |
| `Registry` | Reads a registry value | `RegistryKey`, `RegistryValue`, optional `RegistryHive`, `RegistryWow6432` |
| `Command` | Same as `ExecutableVersion`, for things that are not really "a version flag" | `Path` |
| `AssumePresent` | Always satisfied. For a component guaranteed by machine policy | |
| `AssumeMissing` | Always missing. For exercising the install path | |

`Path` expands environment variables, and a bare name is resolved on `PATH` using `PATHEXT`.
`VersionPattern` is a regular expression with a `version` capture group; without one, Phoenix
looks for the first `1.2` or `1.2.3` in the output. The .NET strategy prefers the filesystem
because plenty of machines have the runtime installed and `dotnet` nowhere on `PATH`.

Detection runs concurrently - it only reads. Installation is serial.

To add a strategy of your own, implement `IPrerequisiteDetector` and register it; a later
registration replaces a built-in one with the same `Strategy`.

## Installers

Order of operations, always:

1. Resolve the installer: `LocalPath` if set, otherwise download `Url` into the cache.
2. Verify its SHA-256. `Security:RequireChecksum` makes this mandatory, and configuration
   validation refuses a `Url` without a `Sha256`, so an unverified installer cannot be
   configured by accident.
3. Verify the Authenticode signature, if `Security:RequireSignature` is on.
4. Run it with `SilentArguments`, bounded by `TimeoutSeconds`.
5. Classify the exit code.
6. Detect again, and only then call it installed.

Step 6 matters: installers have been known to report success and leave nothing behind.

Arguments are passed as a list, never as a shell string, so a path with spaces cannot turn into
extra arguments.

### Getting the checksum

Download the installer once, by hand, from a source you trust:

```powershell
Invoke-WebRequest https://www.python.org/ftp/python/3.12.4/python-3.12.4-amd64.exe -OutFile python.exe
Get-FileHash python.exe -Algorithm SHA256
```

Pin a versioned URL, not a "latest" redirect - a redirect's content changes underneath the hash.

The default `appsettings.json` ships the .NET runtime prerequisite as **detection only**: it
reports a missing runtime clearly rather than carrying a URL and checksum that would go stale.
Worked installer examples are in
[../config/examples/prerequisites.windows.json](../config/examples/prerequisites.windows.json).

### Exit codes

| Code | Meaning |
| --- | --- |
| In `AcceptedExitCodes` (default `[0]`) | Installed |
| In `RebootExitCodes` (default `[3010, 1641]`) | Installed, restart required |
| `1223` from an elevated run | The user declined the elevation prompt |
| Anything else | Failed; the code and output go to the log |

## Elevation

Phoenix runs as the signed-in user - its manifest says `asInvoker` - and stays that way.

When a prerequisite is marked `RequiresAdministrator` and Phoenix is not elevated, the
*installer* is launched with the `runas` verb, so Windows shows one UAC prompt for that
component. Declining is a decision, not a crash: Phoenix reports which component needs
permission and exits cleanly.

## Reboots

When an installer returns 3010 or 1641, Phoenix:

1. records `PendingReboot` in its state file;
2. tells the user which components need the restart;
3. exits with code 8;
4. does **not** install or activate the application - a partially prepared machine is not a
   good place to switch versions.

After the restart, running Phoenix again re-detects everything. Components that really did
install are found, are not installed a second time, and the run continues where it stopped.

## Releases that declare their own prerequisites

A release manifest can list prerequisites by id, with a `minimumVersion`:

```json
"prerequisites": [{ "id": "aspnetcore-runtime", "minimumVersion": "9.0.0" }]
```

A release can *raise* the minimum version of a component this machine already knows about. It
cannot introduce a component the configuration has never heard of - an unknown id is reported
as unknown rather than silently trusted. This runs after the manifest is validated and before
anything large is downloaded.

## Testing without touching the machine

`Testing:SimulatePrerequisiteInstalls` (Development only) logs what would have been run and
reports success without executing anything. Automated tests never change machine-wide software;
the integration suite detects the real runtime but has `InstallMissing` turned off.
