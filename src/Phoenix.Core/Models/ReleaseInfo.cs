using Phoenix.Core.Versioning;

namespace Phoenix.Core.Models;

/// <summary>A release as published by the distribution source (GitHub Releases today).</summary>
public sealed record ReleaseInfo
{
    public required string Tag { get; init; }

    public required SemanticVersion Version { get; init; }

    public string? Name { get; init; }

    /// <summary>The source's own prerelease flag. Used as a cross-check against the tag.</summary>
    public bool IsPrerelease { get; init; }

    public bool IsDraft { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    public ReleaseAsset? FindAsset(string fileName) =>
        Assets.FirstOrDefault(a => string.Equals(a.Name, fileName, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Tag;
}

/// <summary>A downloadable file attached to a release.</summary>
public sealed record ReleaseAsset
{
    public required string Name { get; init; }

    public required Uri DownloadUrl { get; init; }

    /// <summary>
    /// The API-side asset URL. Private repositories can only be read through this one,
    /// with an <c>Accept: application/octet-stream</c> header and a token.
    /// </summary>
    public Uri? ApiUrl { get; init; }

    public long SizeBytes { get; init; }

    public string? ContentType { get; init; }
}
