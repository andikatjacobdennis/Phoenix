using Phoenix.Core.Versioning;

namespace Phoenix.Core.Models;

/// <summary>
/// A release that passed channel and asset filtering and is therefore worth attempting.
/// The selector produces an ordered list: newest first, then bounded remote fallbacks.
/// </summary>
public sealed record ReleaseCandidate
{
    public required ReleaseInfo Release { get; init; }

    /// <summary>The <c>release-manifest.json</c> asset of this release.</summary>
    public required ReleaseAsset ManifestAsset { get; init; }

    public SemanticVersion Version => Release.Version;

    public override string ToString() => Release.Tag;
}

/// <summary>Why Phoenix decided to install, skip, or do nothing at all.</summary>
public enum UpdateAction
{
    /// <summary>Nothing installed locally: this is a first installation.</summary>
    FreshInstall,

    /// <summary>A newer release exists and policy allows installing it.</summary>
    Update,

    /// <summary>The installed version is current (or newer); just launch it.</summary>
    UpToDate,

    /// <summary>An update exists but policy forbids installing it automatically.</summary>
    UpdateAvailableButBlocked,

    /// <summary>No release could be found; fall back to whatever is installed.</summary>
    NoReleaseAvailable,
}

public sealed record UpdateDecision
{
    public required UpdateAction Action { get; init; }

    public InstallationInfo? Installed { get; init; }

    public IReadOnlyList<ReleaseCandidate> Candidates { get; init; } = [];

    public ReleaseCandidate? Target => Candidates.Count > 0 ? Candidates[0] : null;

    public string? Explanation { get; init; }
}
