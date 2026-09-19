# Recovery

The design goal is blunt: **it should be very hard to leave a machine with no working
application**. Everything below follows from that.

## What "known good" means

A version becomes known good only when all four of these are true:

1. the package's SHA-256 matched;
2. it extracted cleanly and contained the executable the manifest named;
3. the process started and was still alive after the startup grace period;
4. the health endpoint answered.

Downloading successfully proves nothing. Starting successfully proves almost nothing - plenty
of applications exit one second later because a port is taken. Only a health check counts.

When a version becomes known good, Phoenix also copies it to `backup/{version}`. That copy is
independent of `versions/{version}`, so a damaged or partly deleted active directory still has
something to recover from.

## The recovery chain

When an installation fails, Phoenix works down this list:

```
1. another remote release   bounded by Updates:RemoteFallbackCount
2. versions/{knownGood}     already on disk, no network needed
3. backup/{knownGood}       copied back into versions/
4. any other usable version newest first, excluding the one that just failed
5. give up loudly           exit code 6, with a reference and a log
```

Steps 2 to 4 need no network at all, which is the point: the most likely time to need recovery
is when something external is broken.

Two names for two different things:

- **Remote fallback** - "this release is bad, try an older *release*".
- **Rollback** - "restore the previous *local* installation".

Remote fallback comes first, because an older release that is known to work is better than an
older *installation* that may be further behind. Rollback is the floor beneath it.

## What the user sees

```
Phoenix

The new version couldn't be started.

Restoring your previous version...

Restoring                                                                 1.4.0
Starting                                                                      ✓
Checking the application                                                      ✓

The update was not applied, but your previous working version has been restored.

Reference: PX-7F2A91
```

No stack trace, no URL, no path. The exception, the failing release, the HTTP status, the
health-check attempts and the exit code are all in the log under that reference.

Exit code 5 means "the update failed and your application still works". Exit code 6 means
"recovery failed too" - that is the one that needs a person.

## State

`state/phoenix-state.json`:

```json
{
  "schemaVersion": 1,
  "installedVersion": "1.5.0",
  "knownGoodVersion": "1.4.0",
  "installationPath": "C:\\Users\\sarah\\AppData\\Local\\Phoenix\\ExampleApp\\versions\\1.5.0",
  "knownGoodPath": "...\\versions\\1.4.0",
  "lastSuccessfulUpdate": "2026-01-15T10:04:11+00:00",
  "pendingOperation": null,
  "pendingReboot": false,
  "prerequisites": [{ "id": "aspnetcore-runtime", "detectedVersion": "9.0.20" }],
  "rejectedVersions": ["1.5.1"],
  "recovery": null
}
```

Writes are atomic: serialise to `.tmp`, flush to the device, then `File.Replace`, which keeps
the previous file as `.bak`. A power cut can lose the newest state; it cannot leave a
half-written file behind.

`rejectedVersions` is how a broken release stays broken. A version that failed its health check
is not retried on the next run, so a machine does not reinstall the same bad build every
morning. The list is capped at the 20 most recent.

## When state is lost

If `phoenix-state.json` is unreadable, Phoenix tries `.bak`, and if that fails too it rebuilds
what it can from the filesystem:

- every directory under `versions/` that has a valid descriptor and the executable it names is
  a real installation;
- the newest of those becomes the active version;
- a version that also exists under `backup/` must once have passed a health check, which is the
  best available evidence of "known good".

The run then continues. An integration test shreds the state file and its backup between two
runs and asserts that the application still comes back up.

## Power loss and interruption

| When it happens | Result |
| --- | --- |
| During download | The partial file is `.part` and is deleted; nothing was overwritten |
| During extraction | Staging is discarded; `versions/` was never touched |
| Just before activation | The old version is still active; the new one sits unused in `versions/` |
| During activation | The state write either happened or it did not - there is no third outcome |
| After activation, before the health check | The next run finds an active version that fails its health check and rolls back |

Cancellation (Ctrl+C) is propagated through every async call, except the activation write and
the rollback itself, which run with a non-cancellable token. Stopping in the middle of a switch
is the one thing that must not happen.

## Cleanup

Cleanup runs after a successful launch and is deliberately timid:

- staging directories older than an hour;
- cache entries older than 14 days, and `.part` files;
- versions beyond `Recovery:KeepVersions`;
- backups beyond `Recovery:BackupRetention`.

The active version and the known-good version are always excluded, and a cleanup failure is a
warning - it never turns a successful installation into a failed one.

## Testing it

See [testing.md](testing.md). The integration suite covers an unhealthy release, a release that
crashes on startup, a corrupt package with fallback to an older release, an unreachable release
source, corrupt state, and explicit `--rollback`.
