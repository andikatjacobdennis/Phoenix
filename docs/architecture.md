# Architecture

## Shape of the solution

```
src/
  Phoenix.Core/            Domain: versions, channels, manifests, state, errors, orchestration
  Phoenix.Infrastructure/  Everything that touches the world: HTTP, disk, registry, processes, console
  Phoenix/                 The executable: command line, configuration, Serilog, DI wiring
  Phoenix.DemoApp/         A real ASP.NET Core application, so the whole cycle can be proven

tests/
  Phoenix.Tests/           Unit tests
  Phoenix.IntegrationTests/End-to-end tests against a real HTTP server and real processes
```

Three source projects, not ten. The split is the one that earns its keep:

- **Core** has no IO. Release selection, channel rules, manifest validation, version
  comparison and the orchestrator are all pure enough to test directly, which is why the
  decisions that matter most (which release, which channel, roll back or not) are the
  cheapest things to test.
- **Infrastructure** is where anything can fail: sockets, disks, registry hives, other
  people's installers. Each class there turns an exception into a `PhoenixError` with a user
  message and a retry classification.
- **Phoenix** composes the two and owns nothing else.

Abstractions exist where there is a real second implementation or a real test seam
(`IReleaseProvider`, `IUserInterface`, `IPrerequisiteDetector`). There is no interface for the
sake of symmetry.

## Deployment model, and the bootstrapper paradox

Phoenix installs .NET runtimes. If Phoenix itself needed one, it could not run on the machine
it is supposed to fix.

So Phoenix publishes **self-contained, single file**:

```powershell
dotnet publish src/Phoenix -c Release -r win-x64 -p:SelfContained=true
```

That is what `scripts/package.ps1` produces and what ships as `phoenix-win-x64.zip` - roughly
31 MB, no dependencies, no installer required for the installer.

The managed application is independent: `Phoenix.DemoApp` publishes **framework-dependent**,
and the ASP.NET Core runtime it needs is a prerequisite Phoenix detects and installs. That is
the whole point of the exercise, and keeping the two deployment models separate is deliberate.

For day-to-day development, `dotnet run --project src/Phoenix` works normally.

## Directory layout Phoenix owns

```
{Directories.Root}/
  versions/1.4.0/          an installed version, with .phoenix-install.json beside it
  versions/1.5.0/
  backup/1.4.0/            an independent copy of the last known-good version
  staging/PX-7F2A91-1.5.0/ in-flight download and extraction, deleted afterwards
  cache/1.5.0/             verified packages, reused instead of downloading again
  logs/                    Serilog files, plus the application's own output
  state/phoenix-state.json what is installed, what is known good, what was in flight
  current -> versions/1.5.0  a convenience link; nothing depends on it
```

Activation is **not** a directory rename. It is one atomic write of `phoenix-state.json`
(write to `.tmp`, flush to disk, `File.Replace` keeping a `.bak`). A version is active because
state says so. That write either happens or it does not, which is the only way to make
"switch to the new version" survive a power cut.

## The run

```
start
  ↓
parse command line, load and validate configuration        → exit 3 if invalid
  ↓
acquire the single-instance lock                           → exit 7 if held
  ↓
welcome, inspect the machine, check disk space
  ↓
prerequisites: detect (concurrently) → install (serially) → re-detect
  ↓                                                        → exit 4 / exit 8 (reboot)
read local state and installed versions
  ↓
query the release source, filter by channel, order, bound the fallback list
  ↓
decide: fresh install / update / up to date / blocked / nothing available
  ↓
for each candidate, newest first:
    resolve and validate the manifest
    check the release's own prerequisites
    check disk space
    download → verify SHA-256 → extract to staging → validate → move into versions/
    stop the old process, activate (atomic state write)
    start → poll health
      healthy   → mark known good, clean up, open the browser, exit 0
      unhealthy → reject this version, try the next candidate
  ↓
no candidate worked → restore the known-good version, start it, explain → exit 5
                      (or exit 6 if recovery itself failed)
```

Two different things, deliberately named differently:

- **Remote fallback** - this release is bad, try an older *release*. Bounded by
  `Updates.RemoteFallbackCount`.
- **Rollback** - restore the previous *local* installation. Needs no network at all.

Remote fallback is attempted first; rollback is the floor beneath it.

## Failure model

Every failure is a `PhoenixError` carrying:

| Field | Used for |
| --- | --- |
| `Category` | Configuration, Network, GitHub, Download, Security, Verification, Prerequisite, Installation, Startup, HealthCheck, Recovery, Permission, DiskSpace, Cancellation |
| `Code` | `PX-VERIFY-MISMATCH` and friends: stable, greppable, in the log |
| `UserMessage` | One calm sentence. No paths, no URLs, no exception text |
| `TechnicalMessage` | Everything: status codes, paths, exit codes, expected vs actual hashes |
| `IsRetryable` | A 503 is; a bad checksum never is |
| `AllowsRemoteFallback` | Whether trying an older release makes sense |
| `RequiresRollback` | Whether the local installation may have been disturbed |
| `ExitCode` | What the process returns |

The factories on `PhoenixError` set these defaults in one place, so "is a checksum failure
retryable?" is answered once rather than at every call site.

## Concurrency

Detection runs concurrently because it only reads. Installation is strictly serial because it
writes. One named mutex per installation root keeps two Phoenix processes apart; a mutex
abandoned by a crashed process is taken over, and state is repaired from disk.

## Where to start reading

| Question | File |
| --- | --- |
| What happens, in order? | `src/Phoenix.Core/Orchestration/PhoenixOrchestrator.cs` |
| Which release gets installed? | `src/Phoenix.Core/Releases/ReleaseSelector.cs` |
| Why was a release rejected? | `src/Phoenix.Core/Releases/ManifestValidator.cs` |
| How does an install avoid breaking things? | `src/Phoenix.Infrastructure/Installation/ReleaseInstaller.cs` |
| What does rollback actually do? | `src/Phoenix.Infrastructure/Installation/InstallationManager.cs` |
| Why does the console look like that? | `src/Phoenix.Infrastructure/Ui/SpectreUserInterface.cs` |
