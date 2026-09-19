using System.Text.Json.Serialization;

namespace Phoenix.Core.Models;

/// <summary>
/// Machine-readable description of a release, published alongside the packages as
/// <c>release-manifest.json</c>. Phoenix treats this as untrusted input: every field is
/// validated before anything is extracted or executed.
/// </summary>
public sealed record ReleaseManifest
{
    public const string DefaultFileName = "release-manifest.json";

    public int SchemaVersion { get; init; } = 1;

    /// <summary>Must equal the configured <c>Application:Id</c>, otherwise the release is rejected.</summary>
    public string ApplicationId { get; init; } = string.Empty;

    public string ApplicationName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ReleaseChannel Channel { get; init; } = ReleaseChannel.Production;

    public IReadOnlyList<ManifestPackage> Packages { get; init; } = [];

    public IReadOnlyList<ManifestPrerequisite> Prerequisites { get; init; } = [];

    /// <summary>Minimum Phoenix version able to install this release.</summary>
    public string? MinimumPhoenixVersion { get; init; }

    public IReadOnlyDictionary<string, string> ReleaseMetadata { get; init; } =
        new Dictionary<string, string>();

    public ManifestPackage? FindPackage(string operatingSystem, string architecture) =>
        Packages.FirstOrDefault(p =>
            string.Equals(p.OperatingSystem, operatingSystem, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Architecture, architecture, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One installable artifact for a specific operating system and architecture.</summary>
public sealed record ManifestPackage
{
    /// <summary>Lower-case RID-style operating system, e.g. <c>win</c>, <c>linux</c>, <c>osx</c>.</summary>
    public string OperatingSystem { get; init; } = string.Empty;

    /// <summary>Lower-case architecture, e.g. <c>x64</c>, <c>arm64</c>.</summary>
    public string Architecture { get; init; } = string.Empty;

    /// <summary>Asset file name attached to the release. Must not contain path separators.</summary>
    public string PackageFile { get; init; } = string.Empty;

    public string PackageFormat { get; init; } = "zip";

    /// <summary>Lower- or upper-case hex SHA-256 of <see cref="PackageFile"/>.</summary>
    public string Sha256 { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    /// <summary>Executable path relative to the extracted package root.</summary>
    public string Executable { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Health endpoint path, e.g. <c>/health</c>.</summary>
    public string? HealthEndpoint { get; init; }

    /// <summary>Environment variables applied when launching the application.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>A prerequisite the release declares as required, referenced by configured id.</summary>
public sealed record ManifestPrerequisite
{
    public string Id { get; init; } = string.Empty;

    public string? MinimumVersion { get; init; }
}
