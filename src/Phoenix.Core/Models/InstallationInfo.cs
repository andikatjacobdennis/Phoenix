using Phoenix.Core.Versioning;

namespace Phoenix.Core.Models;

/// <summary>A version present on disk, discovered from state or by scanning the versions folder.</summary>
public sealed record InstallationInfo
{
    public required SemanticVersion Version { get; init; }

    public required string Path { get; init; }

    /// <summary>Executable path relative to <see cref="Path"/>.</summary>
    public required string RelativeExecutable { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? HealthEndpoint { get; init; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();

    /// <summary>True when this version previously started and passed a health check.</summary>
    public bool IsKnownGood { get; init; }

    public string ExecutablePath => System.IO.Path.Combine(Path, RelativeExecutable);

    public override string ToString() => $"{Version} ({Path})";
}

/// <summary>What Phoenix learned about the machine before it started work.</summary>
public sealed record SystemInformation
{
    public required string UserDisplayName { get; init; }

    public required string MachineName { get; init; }

    public required string OperatingSystem { get; init; }

    /// <summary>RID-style operating system moniker used for package selection: <c>win</c>, <c>linux</c>, <c>osx</c>.</summary>
    public required string OperatingSystemMoniker { get; init; }

    public required string Architecture { get; init; }

    public required bool IsElevated { get; init; }

    public required bool IsInteractive { get; init; }

    public required string PhoenixVersion { get; init; }

    public string? DotNetRuntimeVersion { get; init; }
}
