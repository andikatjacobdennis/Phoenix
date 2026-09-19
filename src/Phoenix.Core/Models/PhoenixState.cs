using System.Text.Json.Serialization;

namespace Phoenix.Core.Models;

/// <summary>
/// Durable record of what Phoenix has done to this machine. Written atomically; if it is
/// ever lost or corrupted, <see cref="Abstractions.IStateStore"/> rebuilds what it can from disk.
/// </summary>
public sealed record PhoenixState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The version Phoenix currently considers active.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>The newest version that actually started and passed a health check.</summary>
    public string? KnownGoodVersion { get; init; }

    /// <summary>Absolute path of the active installation.</summary>
    public string? InstallationPath { get; init; }

    /// <summary>Absolute path of the known-good installation kept for recovery.</summary>
    public string? KnownGoodPath { get; init; }

    public DateTimeOffset? LastSuccessfulUpdate { get; init; }

    public DateTimeOffset? LastUpdateCheck { get; init; }

    /// <summary>Set while a multi-step operation is in flight, so the next run can resume or repair.</summary>
    public PendingOperation? PendingOperation { get; init; }

    public bool PendingReboot { get; init; }

    /// <summary>Prerequisites already satisfied, so a reboot-resume does not reinstall them.</summary>
    public IReadOnlyList<CompletedPrerequisite> Prerequisites { get; init; } = [];

    /// <summary>Releases that failed to become healthy. Phoenix does not try them again.</summary>
    public IReadOnlyList<string> RejectedVersions { get; init; } = [];

    public RecoveryInformation? Recovery { get; init; }

    public static PhoenixState Empty { get; } = new();

    public PhoenixState WithRejected(string version)
    {
        if (RejectedVersions.Contains(version, StringComparer.OrdinalIgnoreCase))
        {
            return this;
        }

        // Keep the list bounded: only recent rejections are useful.
        var rejected = RejectedVersions.Append(version).TakeLast(20).ToList();
        return this with { RejectedVersions = rejected };
    }
}

/// <summary>What Phoenix was doing when it last wrote state.</summary>
public sealed record PendingOperation
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PendingOperationKind Kind { get; init; }

    public string? TargetVersion { get; init; }

    public string? StagingPath { get; init; }

    public string? PrerequisiteId { get; init; }

    public string? OperationId { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum PendingOperationKind
{
    None = 0,
    Download,
    Extract,
    Activate,
    PrerequisiteInstall,
    AwaitingReboot,
    Rollback,
}

public sealed record CompletedPrerequisite
{
    public string Id { get; init; } = string.Empty;

    public string? DetectedVersion { get; init; }

    public DateTimeOffset InstalledAt { get; init; }
}

public sealed record RecoveryInformation
{
    public string? RestoredFromVersion { get; init; }

    public string? FailedVersion { get; init; }

    public string? Reason { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }
}
