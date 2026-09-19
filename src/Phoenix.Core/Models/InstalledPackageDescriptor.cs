namespace Phoenix.Core.Models;

/// <summary>
/// Written into every installed version directory as <c>.phoenix-install.json</c>.
///
/// It records what the manifest said, so a later run can launch, health check and recover the
/// version without any network access. Recovery must work when GitHub cannot be reached.
/// </summary>
public sealed record InstalledPackageDescriptor
{
    public const string FileName = ".phoenix-install.json";

    public int SchemaVersion { get; init; } = 1;

    public string ApplicationId { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public ReleaseChannel Channel { get; init; }

    /// <summary>Executable path relative to the version directory.</summary>
    public string Executable { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? HealthEndpoint { get; init; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();

    /// <summary>The release tag this version came from. Diagnostics only.</summary>
    public string? SourceTag { get; init; }

    /// <summary>SHA-256 of the package that produced this directory. Diagnostics only.</summary>
    public string? PackageSha256 { get; init; }

    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;
}
