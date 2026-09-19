# Development

## Getting started

```bash
git clone https://github.com/andikatjacobdennis/Phoenix.git
cd Phoenix
dotnet restore
dotnet build
dotnet test
```

You need the .NET SDK 9.0 or newer on Windows. `global.json` rolls forward, so a newer SDK
works; the projects target `net9.0` (Core, DemoApp) and `net9.0-windows` (Infrastructure,
Phoenix, tests).

## Running Phoenix while developing

```bash
dotnet run --project src/Phoenix -- --environment Development --diagnostics --no-browser
```

The Development environment uses a separate installation root
(`%LOCALAPPDATA%\Phoenix\dev\...`), a separate port, and allows fault injection. It cannot
disturb a real installation.

Use `config/examples/appsettings.local-testing.json` with `--config` to point at a local
release server instead of GitHub - see [testing.md](testing.md).

## Project layout

| Project | Target | Contains |
| --- | --- | --- |
| `Phoenix.Core` | `net9.0` | Versions, channels, manifests, state, errors, options, orchestration. No IO |
| `Phoenix.Infrastructure` | `net9.0-windows` | HTTP, filesystem, registry, processes, Spectre.Console |
| `Phoenix` | `net9.0-windows` | Command line, configuration, Serilog, DI |
| `Phoenix.DemoApp` | `net9.0` | A real ASP.NET Core application |

## Conventions

- Nullable reference types are on; a few nullability warnings are errors (see
  `Directory.Build.props`).
- Async all the way down, with `CancellationToken` on anything that can wait. No `.Result`, no
  `.Wait()`, no `async void`.
- Failures are returned as `Result` / `Result<T>` carrying a `PhoenixError`, not thrown.
  Exceptions are caught at the boundary that produced them and converted there.
- Structured logging with named properties: `_logger.LogInformation("Activated {Version}", v)`.
- Options are strongly typed and validated once, at startup.
- New packages need a reason. Central versions live in `Directory.Packages.props`.

The analyzer set is `latest-recommended`. Suppressions are listed with their reasons in
`Directory.Build.props`; add to that list only with a reason worth reading.

## Versioning

`Directory.Build.props` takes one property and stamps everything from it:

```bash
dotnet build -p:PhoenixVersion=1.4.0-qa.2
```

Local builds default to `0.1.0-dev`. Never hard-code a version anywhere else - Phoenix and the
demo application both read their own version from assembly metadata.

## Adding things

**A detection strategy.** Add a value to `DetectionStrategy`, implement
`IPrerequisiteDetector`, register it in `PhoenixServiceCollectionExtensions`. A later
registration replaces a built-in one with the same strategy. Extend the validator if the new
strategy needs particular configuration fields.

**A release source that is not GitHub.** Implement `IReleaseProvider`
(`GetReleasesAsync` plus `DescribeAsset`) and register it instead. Nothing else in Phoenix
knows what GitHub is.

**A manifest field.** Add it to `ReleaseManifest` or `ManifestPackage`, validate it in
`ManifestValidator`, emit it in `scripts/package.ps1`, and check it in
`scripts/Test-ReleaseArtifacts.ps1`. Bump `schemaVersion` only for a breaking change - Phoenix
refuses a schema it does not understand.

**A console element.** Add it to `IUserInterface` first, then implement it in
`SpectreUserInterface` and in the tests' `RecordingUserInterface`. Keeping the interface narrow
is what stops paths and stack traces leaking onto the screen.

## Things worth knowing

- **Activation is a state write.** If you find yourself renaming directories to switch
  versions, stop: that is the thing this design avoids.
- **The launched application outlives Phoenix.** Disposing `IApplicationProcess` releases the
  handle and nothing else. Stopping the application is always explicit.
- **The child inherits the console pipes.** Piping Phoenix's stdout into another command blocks
  until the application exits. Redirect to a file when scripting.
- **Fault injection is Development-only** and enforced by configuration validation, not by
  convention.
- **Cleanup is never fatal.** A failed delete is a warning; a successful installation is not
  thrown away over a locked file.

## Before opening a pull request

```bash
dotnet build -warnaserror
dotnet test
./scripts/package.ps1 -Version 0.0.1-dev.1 -OutputDirectory ./artifacts/check
./scripts/Test-ReleaseArtifacts.ps1 -ArtifactDirectory ./artifacts/check
```

CI runs exactly this on `windows-latest`.
