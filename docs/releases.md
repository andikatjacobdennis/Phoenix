# Releases

## Channels

A Production machine must never install a Development build. Phoenix makes that a rule, not a
convention, and the rule is derived from the version itself.

| Tag | Channel |
| --- | --- |
| `v1.4.0` | Production |
| `v1.5.0-qa.2`, `-beta.1`, `-rc.1` | QA |
| `v1.6.0-dev.7`, `-alpha.1`, anything else | Development |

Three independent checks have to agree before a release is installed:

1. the channel derived from the tag matches `Updates:Channel`;
2. the GitHub prerelease flag matches whether the tag has a prerelease label - a stable tag
   published as a prerelease is a broken pipeline, and Phoenix refuses to guess;
3. the manifest's own `channel` field matches both.

Channels do not inherit. A QA machine takes QA releases only, not Production ones. That is
deliberate: QA exists to test what QA will ship.

## Versioning

Semantic Versioning 2.0.0, and **CI is the source of truth**. `Directory.Build.props` takes a
single `PhoenixVersion` property and stamps it into every assembly:

```bash
dotnet build -p:PhoenixVersion=1.4.0
```

Local builds default to `0.1.0-dev`. Nothing hard-codes a version string: Phoenix reports its
own version from assembly metadata, the demo application reports its version the same way, and
the manifest is generated from the same input. One number, everywhere.

Ordering follows the specification, including that `1.0.0-rc.1` sorts below `1.0.0`, that
numeric prerelease identifiers compare numerically (`qa.2` < `qa.10`), and that build metadata
is ignored.

One deliberate exception: `minimumPhoenixVersion` compares against Phoenix's release version
only, so a `1.2.0-dev` build of the installer can install a release that asks for `1.2.0`.
Ordering still uses full precedence; this is a floor, not a sort.

## The release manifest

Every release carries `release-manifest.json`. Phoenix reads it first and treats it as
untrusted input.

```json
{
  "schemaVersion": 1,
  "applicationId": "phoenix-demoapp",
  "applicationName": "Phoenix Demo Application",
  "version": "1.1.0",
  "channel": "Production",
  "packages": [
    {
      "operatingSystem": "win",
      "architecture": "x64",
      "packageFile": "phoenix-demoapp-win-x64.zip",
      "packageFormat": "zip",
      "sha256": "23e2615ce298989f538349d19a2f6abecae11fff265c3fbe0f43eb025ab8f610",
      "sizeBytes": 90030,
      "executable": "Phoenix.DemoApp.exe",
      "arguments": ["--urls", "http://localhost:5080"],
      "healthEndpoint": "/health",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Production" }
    }
  ],
  "prerequisites": [{ "id": "aspnetcore-runtime", "minimumVersion": "9.0.0" }],
  "minimumPhoenixVersion": "0.1.0",
  "releaseMetadata": { "builtAt": "...", "commit": "...", "runtime": "win-x64", "builtBy": "..." }
}
```

A release is rejected outright if:

| Check | Error code |
| --- | --- |
| `applicationId` is not the configured one | `PX-MANIFEST-APPID` |
| `version` disagrees with the tag | `PX-MANIFEST-VERSION-MISMATCH` |
| `channel` disagrees with configuration | `PX-MANIFEST-CHANNEL` |
| `channel` disagrees with its own version | `PX-MANIFEST-CHANNEL-TAG` |
| `executable` is absolute, drive-qualified or contains `..` | `PX-MANIFEST-EXECUTABLE` |
| `packageFile` is anything but a plain file name | `PX-MANIFEST-PACKAGE-NAME` |
| `sha256` is missing or not 64 hex characters | `PX-MANIFEST-SHA` |
| there is no package for this OS and architecture | `PX-MANIFEST-NO-PACKAGE` |
| the release needs a newer Phoenix | `PX-MANIFEST-PHOENIX-TOO-OLD` |

The `executable` check is the important one. Phoenix runs what the manifest names, so remote
content must never be able to name `C:\Windows\System32\cmd.exe` or climb out of the package.

## Assets

| Asset | Contents |
| --- | --- |
| `phoenix-win-x64.zip` | Phoenix, self-contained single file |
| `phoenix-demoapp-win-x64.zip` | The application, framework-dependent |
| `release-manifest.json` | The description above |
| `SHA256SUMS` | `<hash> *<file>`, one line per package |

A release with no manifest asset is skipped: Phoenix will not guess which file to install.

## Publishing

### With GitHub Actions

```bash
git tag v1.1.0
git push origin v1.1.0
```

`.github/workflows/release.yml` derives the version and channel from the tag, builds, runs both
test suites, packages, validates the artifacts against the manifest, and only then creates the
GitHub release. Prerelease channels are marked as prereleases. Nothing is published if an
earlier step fails.

`workflow_dispatch` accepts a version directly, for a release built from a branch.

### By hand

```powershell
./scripts/package.ps1 -Version 1.1.0 -OutputDirectory ./artifacts/1.1.0
./scripts/Test-ReleaseArtifacts.ps1 -ArtifactDirectory ./artifacts/1.1.0 -ExpectedVersion 1.1.0

gh release create v1.1.0 (Get-ChildItem ./artifacts/1.1.0 -File) --title "Production 1.1.0"
```

`-SkipPhoenix` packages only the application, which is what you want when shipping an
application update without a new bootstrapper.

## Private repositories

Set the environment variable named by `GitHub:TokenEnvironmentVariable` (default
`PHOENIX_GITHUB_TOKEN`) to a token with `contents: read`. Phoenix then switches to the API
asset endpoints, which is the only way private assets can be fetched. The token is read from
the environment at the moment of use, applied per request, and never written to configuration
or to the log. If a repository returns 401 or 403, the error names the variable to set - it
never echoes the value.
