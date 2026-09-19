using Phoenix.Core.Versioning;

namespace Phoenix.Core.Models;

/// <summary>Result of asking a detector whether a prerequisite is present.</summary>
public sealed record DetectionResult
{
    public required bool IsInstalled { get; init; }

    public SemanticVersion? DetectedVersion { get; init; }

    /// <summary>All versions found, when the detector can enumerate several (e.g. .NET runtimes).</summary>
    public IReadOnlyList<SemanticVersion> AllDetectedVersions { get; init; } = [];

    /// <summary>Where it was found. Diagnostics only, never shown in normal mode.</summary>
    public string? Evidence { get; init; }

    public static DetectionResult NotFound(string? evidence = null) =>
        new() { IsInstalled = false, Evidence = evidence };

    public static DetectionResult Found(SemanticVersion version, string? evidence = null) =>
        new()
        {
            IsInstalled = true,
            DetectedVersion = version,
            AllDetectedVersions = [version],
            Evidence = evidence,
        };
}

public enum PrerequisiteState
{
    /// <summary>Present and within the configured version range.</summary>
    Satisfied,

    /// <summary>Not present, or present with an incompatible version.</summary>
    Missing,

    /// <summary>Installed during this run.</summary>
    Installed,

    /// <summary>Installed, but the machine must restart before it can be used.</summary>
    RebootRequired,

    /// <summary>Could not be installed.</summary>
    Failed,

    /// <summary>Not applicable to the current environment, or optional and absent.</summary>
    Skipped,
}

/// <summary>Per-prerequisite outcome, used for both the console table and the logs.</summary>
public sealed record PrerequisiteStatus
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required PrerequisiteState State { get; init; }

    public SemanticVersion? DetectedVersion { get; init; }

    public string? RequirementDescription { get; init; }

    public bool IsMandatory { get; init; } = true;

    public string? Detail { get; init; }
}

/// <summary>Aggregate outcome of the prerequisite phase.</summary>
public sealed record PrerequisiteReport
{
    public IReadOnlyList<PrerequisiteStatus> Items { get; init; } = [];

    public bool RebootRequired => Items.Any(i => i.State == PrerequisiteState.RebootRequired);

    public bool HasMandatoryFailure =>
        Items.Any(i => i.IsMandatory && i.State == PrerequisiteState.Failed);

    public bool AnyInstalled => Items.Any(i => i.State is PrerequisiteState.Installed or PrerequisiteState.RebootRequired);
}
