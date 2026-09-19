using Phoenix.Core.Models;
using Phoenix.Core.Versioning;

namespace Phoenix.Core.Orchestration;

/// <summary>
/// A candidate whose manifest has been downloaded and validated: Phoenix now knows exactly
/// which asset to fetch, which checksum to expect and which executable to run.
/// </summary>
public sealed record ResolvedRelease
{
    public required ReleaseCandidate Candidate { get; init; }

    public required ReleaseManifest Manifest { get; init; }

    public required ManifestPackage Package { get; init; }

    public required ReleaseAsset PackageAsset { get; init; }

    public SemanticVersion Version => Candidate.Version;
}
