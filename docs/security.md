# Security

Phoenix downloads software from the internet and executes it. That is the whole job, so the
interesting question is not "is it secure" but "what specifically stops each way this goes
wrong".

## Threats and controls

| Threat | Control |
| --- | --- |
| Tampered package in transit | HTTPS required (`Security:RequireHttps`); loopback exempt for local testing only |
| Tampered package at rest | SHA-256 from the manifest, verified before extraction. Never retried - the same bytes fail the same way |
| Download redirected elsewhere | Host allow-list (`Security:AllowedDownloadHosts`), checked before the request is made |
| Archive writes outside its directory | Entry names rejected for `..`, absolute paths and drive qualifiers; every resolved path is re-checked against the destination root; one bad entry aborts the whole extraction |
| Zip bomb | Caps on entry count, declared size and bytes actually written |
| Manifest names an arbitrary executable | `executable` must be a relative path inside the package, with no traversal |
| Package belongs to another product | `applicationId` must equal the configured `Application:Id` |
| Development build reaching Production | Channel derived from the tag, cross-checked against the prerelease flag and the manifest's own field |
| Downgrade to a known-bad version | Rejected versions are remembered; downgrades need `Updates:AllowDowngrade` |
| Unsigned or wrongly signed binary | Optional Authenticode verification, with an optional thumbprint allow-list |
| Token leaking into logs or config | Only the *name* of the environment variable is configured; the value is read at the point of use and never logged |
| Two installers corrupting one directory | Named mutex per installation root |
| Running as administrator unnecessarily | Manifest is `asInvoker`; elevation is requested per installer, only when required |
| Command injection through configuration | Arguments are passed as argument lists, never as a shell string; no shell is involved |
| Disk exhaustion mid-install | Space is checked, pessimistically, before anything is downloaded |

## Order of operations

The order is the control. Nothing is executed before it has been verified:

```
manifest → validate → disk space → download → checksum → signature
        → extract (guarded) → validate payload → move into versions/
        → activate → start → health check → known good
```

## Archive extraction

The check that matters, in `ZipPackageExtractor`:

```csharp
var targetPath = Path.GetFullPath(Path.Combine(root, entry.FullName));

if (!targetPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
    !string.Equals(targetPath, root, StringComparison.OrdinalIgnoreCase))
{
    return PhoenixError.Security("PX-EXTRACT-TRAVERSAL", ...);
}
```

Entry names are screened before the path arithmetic, and the resolved path is verified after
it. Both layers are tested: unit tests feed archives containing `../escaped.txt`,
`..\escaped.txt`, `nested/../../escaped.txt`, `/etc/passwd`, `C:\Windows\evil.dll` and
`file.txt:stream`, and assert both that extraction is refused and that nothing appeared outside
the destination. An integration test serves a genuinely malicious package over HTTP and asserts
the same end to end.

## Secrets

Nothing in this repository contains a token, a password or a key, and nothing is expected to.

- The GitHub token is read from the environment variable named by
  `GitHub:TokenEnvironmentVariable` (default `PHOENIX_GITHUB_TOKEN`).
- It is attached per request and never stored, printed or logged. A 401 or 403 names the
  variable to set, never its value.
- CI uses the automatically provided `GITHUB_TOKEN`; no long-lived secret is needed to publish
  a release.
- `.gitignore` excludes `.env`, `*.token`, logs and local installations.

## Signature verification

Off by default, because most internal applications are not signed and a check that always
passes teaches people to ignore it. Turn it on once your packages are signed:

```json
"Security": {
  "RequireSignature": true,
  "AllowedCertificateThumbprints": ["A1B2C3..."]
}
```

Phoenix then verifies the Authenticode chain on the application executable and on every
prerequisite installer, with revocation checking, and refuses anything outside the thumbprint
list when one is configured.

## What Phoenix deliberately does not do

- It does not run as administrator to make things easier.
- It does not disable TLS validation, for any reason.
- It does not execute anything from a package before the package has been verified.
- It does not delete the only known-good copy, whatever the cleanup settings say.
- It does not retry a failed integrity check; corruption and tampering look identical from here.

## Reporting a problem

Open a security advisory on
[github.com/andikatjacobdennis/Phoenix](https://github.com/andikatjacobdennis/Phoenix) rather
than a public issue.
