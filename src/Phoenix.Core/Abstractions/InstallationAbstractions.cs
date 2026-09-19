using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Core.Orchestration;
using Phoenix.Core.Versioning;

namespace Phoenix.Core.Abstractions;

/// <summary>Turns a remote release into a validated, ready-to-activate directory on disk.</summary>
public interface IReleaseInstaller
{
    /// <summary>
    /// Fetches and validates the release manifest. Nothing is downloaded or executed on the
    /// strength of a manifest that has not passed this step.
    /// </summary>
    Task<Result<ResolvedRelease>> ResolveAsync(ReleaseCandidate candidate, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads, verifies, extracts and validates one resolved release. Nothing that is
    /// currently active is touched: the result is a new version directory, not an activation.
    /// </summary>
    Task<Result<InstallationInfo>> InstallAsync(
        ResolvedRelease resolved,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Owns which installed version is active, which one is known good, and the way back.</summary>
public interface IInstallationManager
{
    /// <summary>The version Phoenix currently considers active, or null on a first run.</summary>
    Task<InstallationInfo?> GetActiveInstallationAsync(CancellationToken cancellationToken);

    /// <summary>The newest version that actually started and answered a health check.</summary>
    Task<InstallationInfo?> GetKnownGoodInstallationAsync(CancellationToken cancellationToken);

    IReadOnlyList<InstallationInfo> ListInstalledVersions();

    /// <summary>Makes a prepared version active. The switch itself is one atomic state write.</summary>
    Task<Result> ActivateAsync(InstallationInfo installation, CancellationToken cancellationToken);

    /// <summary>Records that a version started and passed its health check.</summary>
    Task<Result> MarkKnownGoodAsync(InstallationInfo installation, CancellationToken cancellationToken);

    /// <summary>Records that a version failed, so it is not attempted again.</summary>
    Task RejectVersionAsync(SemanticVersion version, string reason, CancellationToken cancellationToken);

    /// <summary>Restores the known-good installation after a failed update.</summary>
    Task<Result<InstallationInfo>> RollbackAsync(
        SemanticVersion? failedVersion,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>Rebuilds what it can from the filesystem when state is missing or corrupt.</summary>
    Task<PhoenixState> RepairStateAsync(CancellationToken cancellationToken);
}

public interface IStateStore
{
    Task<PhoenixState> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(PhoenixState state, CancellationToken cancellationToken);

    /// <summary>Applies a change to the current state and writes it atomically.</summary>
    Task<PhoenixState> UpdateAsync(Func<PhoenixState, PhoenixState> change, CancellationToken cancellationToken);

    /// <summary>True when the state file was unreadable and a fresh one was assumed.</summary>
    bool LastLoadWasRecovered { get; }
}

/// <summary>A running application process.</summary>
public interface IApplicationProcess : IAsyncDisposable
{
    int ProcessId { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>Asks the process to stop, escalating to a kill after the configured timeout.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IApplicationLauncher
{
    Task<Result<IApplicationProcess>> StartAsync(InstallationInfo installation, CancellationToken cancellationToken);

    /// <summary>Finds an already-running instance of this installation, if one exists.</summary>
    IApplicationProcess? FindRunningInstance(InstallationInfo installation);

    /// <summary>Stops any process running out of the given installation directory.</summary>
    Task StopRunningInstancesAsync(string installationPath, CancellationToken cancellationToken);
}

public interface IHealthCheckService
{
    /// <summary>
    /// Polls the health endpoint until it answers, the process dies, the attempt budget runs
    /// out, or the overall timeout expires. Never unbounded.
    /// </summary>
    Task<Result> WaitForHealthyAsync(
        Uri healthUrl,
        IApplicationProcess? process,
        CancellationToken cancellationToken);
}

public interface IBrowserLauncher
{
    Result Open(Uri url);
}

public interface ICleanupService
{
    /// <summary>
    /// Removes abandoned staging folders, expired cache entries, obsolete versions and old
    /// backups. Never removes the active installation or the only known-good copy.
    /// </summary>
    Task CleanupAsync(CancellationToken cancellationToken);
}

/// <summary>Cross-process guard so two Phoenix instances never update the same installation.</summary>
public interface ISingleInstanceManager
{
    Result<IDisposable> TryAcquire(string key);
}

public interface IEnvironmentService
{
    string EnvironmentName { get; }

    SystemInformation GetSystemInformation();

    bool IsElevated { get; }

    /// <summary>Restarts Phoenix elevated. Returns false when the user declined the prompt.</summary>
    bool TryRelaunchElevated(IReadOnlyList<string> arguments);
}

public interface IDiskSpaceService
{
    /// <summary>Checks that <paramref name="path"/> has room for the requested bytes plus the safety margin.</summary>
    Result EnsureAvailable(string path, long requiredBytes);

    long GetAvailableFreeBytes(string path);
}

/// <summary>Detects and installs the frameworks the application needs.</summary>
public interface IPrerequisiteManager
{
    Task<Result<PrerequisiteReport>> EnsureAsync(
        IReadOnlyList<ManifestPrerequisite> releaseRequirements,
        CancellationToken cancellationToken);
}
