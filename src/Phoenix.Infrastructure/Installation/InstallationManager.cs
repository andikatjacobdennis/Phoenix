using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Core.Versioning;
using Phoenix.Infrastructure.Testing;

namespace Phoenix.Infrastructure.Installation;

/// <summary>
/// Decides which installed version is live, keeps a known-good copy, and knows the way back.
///
/// Activation is deliberately a single atomic state write rather than a directory rename:
/// a version becomes active because state says so, and that write either happens or does not.
/// The <c>current</c> junction is maintained as a convenience for people browsing the folder,
/// and nothing depends on it.
/// </summary>
public sealed class InstallationManager : IInstallationManager
{
    private readonly PhoenixPaths _paths;
    private readonly PhoenixOptions _options;
    private readonly IStateStore _state;
    private readonly FaultInjector _faults;
    private readonly ILogger<InstallationManager> _logger;

    public InstallationManager(
        PhoenixPaths paths,
        IOptions<PhoenixOptions> options,
        IStateStore state,
        FaultInjector faults,
        ILogger<InstallationManager> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _paths = paths;
        _options = options.Value;
        _state = state;
        _faults = faults;
        _logger = logger;
    }

    public IReadOnlyList<InstallationInfo> ListInstalledVersions()
    {
        if (!Directory.Exists(_paths.Versions))
        {
            return [];
        }

        var installations = new List<InstallationInfo>();

        foreach (var directory in Directory.EnumerateDirectories(_paths.Versions))
        {
            var installation = InstalledPackageStore.TryReadInstallation(directory, isKnownGood: false);
            if (installation is not null)
            {
                installations.Add(installation);
            }
        }

        return installations.OrderByDescending(i => i.Version).ToList();
    }

    public async Task<InstallationInfo?> GetActiveInstallationAsync(CancellationToken cancellationToken)
    {
        var state = await _state.LoadAsync(cancellationToken).ConfigureAwait(false);
        var installed = ListInstalledVersions();

        if (installed.Count == 0)
        {
            return null;
        }

        var knownGood = state.KnownGoodVersion;

        if (!string.IsNullOrWhiteSpace(state.InstalledVersion))
        {
            var match = installed.FirstOrDefault(i =>
                string.Equals(i.Version.ToString(), state.InstalledVersion, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match with { IsKnownGood = MatchesVersion(match, knownGood) };
            }

            _logger.LogWarning(
                "State says {Version} is installed but that directory is not usable; falling back.",
                state.InstalledVersion);
        }

        // State and disk disagree: prefer the known-good copy, then the newest usable version.
        var fallback = installed.FirstOrDefault(i => MatchesVersion(i, knownGood)) ?? installed[0];
        return fallback with { IsKnownGood = MatchesVersion(fallback, knownGood) };
    }

    public async Task<InstallationInfo?> GetKnownGoodInstallationAsync(CancellationToken cancellationToken)
    {
        var state = await _state.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(state.KnownGoodVersion))
        {
            return null;
        }

        return ResolveVersion(state.KnownGoodVersion);
    }

    public async Task<Result> ActivateAsync(InstallationInfo installation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);

        if (_faults.ShouldFailActivation)
        {
            _logger.LogWarning("Injecting an activation failure for {Version}.", installation.Version);
            return PhoenixError.Installation(
                "PX-ACTIVATE-SIMULATED",
                "Simulated activation failure (Phoenix fault injection).");
        }

        if (!File.Exists(installation.ExecutablePath))
        {
            return PhoenixError.Installation(
                "PX-ACTIVATE-NO-EXE",
                $"Cannot activate {installation.Version}: '{installation.ExecutablePath}' does not exist.");
        }

        try
        {
            await _state.UpdateAsync(
                s => s with
                {
                    InstalledVersion = installation.Version.ToString(),
                    InstallationPath = installation.Path,
                    PendingOperation = null,
                },
                cancellationToken).ConfigureAwait(false);

            UpdateCurrentJunction(installation.Path);

            _logger.LogInformation("Activated version {Version} at {Path}.", installation.Version, installation.Path);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PhoenixError.Installation(
                "PX-ACTIVATE-IO",
                $"Activating {installation.Version} failed: {ex.Message}",
                ex);
        }
    }

    public async Task<Result> MarkKnownGoodAsync(InstallationInfo installation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var state = await _state.LoadAsync(cancellationToken).ConfigureAwait(false);
        var version = installation.Version.ToString();

        if (string.Equals(state.KnownGoodVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success();
        }

        // A second, independent copy of the last version that actually worked. If the active
        // directory is later damaged, recovery still has something to restore from.
        TryCreateBackup(installation);

        await _state.UpdateAsync(
            s => s with
            {
                KnownGoodVersion = version,
                KnownGoodPath = installation.Path,
                InstalledVersion = version,
                InstallationPath = installation.Path,
                LastSuccessfulUpdate = DateTimeOffset.UtcNow,
                PendingOperation = null,
                Recovery = null,
            },
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Version {Version} is now known good.", installation.Version);
        return Result.Success();
    }

    public async Task RejectVersionAsync(
        SemanticVersion version,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);

        _logger.LogWarning("Rejecting version {Version}: {Reason}", version, reason);

        await _state.UpdateAsync(
            s => s.WithRejected(version.ToString()),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<InstallationInfo>> RollbackAsync(
        SemanticVersion? failedVersion,
        string reason,
        CancellationToken cancellationToken)
    {
        var state = await _state.LoadAsync(cancellationToken).ConfigureAwait(false);
        var target = ResolveRollbackTarget(state, failedVersion);

        if (target is null)
        {
            return PhoenixError.Recovery(
                "PX-RECOVERY-NO-TARGET",
                failedVersion is null
                    ? "There is no known-good installation to restore."
                    : $"Version {failedVersion} failed and no known-good installation is available to restore.");
        }

        // Rollback is a recovery step: it must complete even while shutting down.
        var activate = await ActivateAsync(target, CancellationToken.None).ConfigureAwait(false);
        if (activate.IsFailure)
        {
            return Result<InstallationInfo>.Failure(activate.Error);
        }

        await _state.UpdateAsync(
            s => (failedVersion is null ? s : s.WithRejected(failedVersion.ToString())) with
            {
                Recovery = new RecoveryInformation
                {
                    RestoredFromVersion = target.Version.ToString(),
                    FailedVersion = failedVersion?.ToString(),
                    Reason = reason,
                    OccurredAt = DateTimeOffset.UtcNow,
                },
            },
            CancellationToken.None).ConfigureAwait(false);

        _logger.LogWarning(
            "Rolled back to {Version} after {FailedVersion} failed ({Reason}).",
            target.Version,
            failedVersion?.ToString() ?? "the active version",
            reason);

        return target with { IsKnownGood = true };
    }

    private InstallationInfo? ResolveRollbackTarget(PhoenixState state, SemanticVersion? failedVersion)
    {
        // 1. The recorded known-good version, if its directory is still usable.
        if (!string.IsNullOrWhiteSpace(state.KnownGoodVersion))
        {
            var knownGood = ResolveVersion(state.KnownGoodVersion);
            if (knownGood is not null)
            {
                return knownGood;
            }

            // 2. The independent backup copy of that version.
            var restored = TryRestoreFromBackup(state.KnownGoodVersion);
            if (restored is not null)
            {
                return restored;
            }

            _logger.LogWarning(
                "Known-good version {Version} is neither installed nor backed up.",
                state.KnownGoodVersion);
        }

        // 3. Any other usable version, newest first. Better a working older build than nothing.
        return ListInstalledVersions()
            .FirstOrDefault(i => failedVersion is null || i.Version != failedVersion);
    }

    public async Task<PhoenixState> RepairStateAsync(CancellationToken cancellationToken)
    {
        var installed = ListInstalledVersions();
        var active = installed.FirstOrDefault();

        // A version that was backed up must once have been known good, which is the best
        // evidence available after losing the state file.
        var knownGood = installed.FirstOrDefault(i => Directory.Exists(_paths.BackupDirectory(i.Version.ToString())))
                        ?? active;

        var repaired = await _state.UpdateAsync(
            s => s with
            {
                InstalledVersion = active?.Version.ToString(),
                InstallationPath = active?.Path,
                KnownGoodVersion = knownGood?.Version.ToString(),
                KnownGoodPath = knownGood?.Path,
                PendingOperation = null,
            },
            cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "State rebuilt from disk: installed={Installed}, knownGood={KnownGood}, candidates={Count}.",
            repaired.InstalledVersion ?? "none",
            repaired.KnownGoodVersion ?? "none",
            installed.Count);

        return repaired;
    }

    // ---- Helpers ---------------------------------------------------------

    private InstallationInfo? ResolveVersion(string version)
    {
        var directory = _paths.VersionDirectory(version);
        return InstalledPackageStore.TryReadInstallation(directory, isKnownGood: true);
    }

    private static bool MatchesVersion(InstallationInfo installation, string? version) =>
        version is not null &&
        string.Equals(installation.Version.ToString(), version, StringComparison.OrdinalIgnoreCase);

    private void TryCreateBackup(InstallationInfo installation)
    {
        var version = installation.Version.ToString();
        var backupPath = _paths.BackupDirectory(version);

        try
        {
            if (Directory.Exists(backupPath))
            {
                return;
            }

            var temporary = backupPath + ".partial";
            DirectoryOperations.TryDelete(temporary);
            DirectoryOperations.Copy(installation.Path, temporary);
            Directory.Move(temporary, backupPath);

            _logger.LogInformation("Backed up known-good version {Version}.", version);
            PruneBackups(version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing backup weakens recovery but does not invalidate a working installation.
            _logger.LogWarning(ex, "Could not back up version {Version}.", version);
        }
    }

    private InstallationInfo? TryRestoreFromBackup(string version)
    {
        var backupPath = _paths.BackupDirectory(version);
        if (!Directory.Exists(backupPath))
        {
            return null;
        }

        try
        {
            var targetPath = _paths.VersionDirectory(version);
            DirectoryOperations.TryDelete(targetPath);
            DirectoryOperations.Copy(backupPath, targetPath);

            _logger.LogWarning("Restored version {Version} from the backup copy.", version);
            return InstalledPackageStore.TryReadInstallation(targetPath, isKnownGood: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Restoring version {Version} from backup failed.", version);
            return null;
        }
    }

    private void PruneBackups(string keepVersion)
    {
        if (!Directory.Exists(_paths.Backup))
        {
            return;
        }

        var backups = Directory.EnumerateDirectories(_paths.Backup)
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .Where(b => SemanticVersion.TryParse(b.Name, out _))
            .OrderByDescending(b => SemanticVersion.Parse(b.Name))
            .ToList();

        foreach (var backup in backups.Skip(Math.Max(1, _options.Recovery.BackupRetention)))
        {
            if (string.Equals(backup.Name, keepVersion, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (DirectoryOperations.TryDelete(backup.Path))
            {
                _logger.LogInformation("Removed old backup {Version}.", backup.Name);
            }
        }
    }

    /// <summary>
    /// Points <c>{Root}/current</c> at the active version. Best effort only: creating a
    /// junction can fail on a restricted machine and that must not fail an installation.
    /// </summary>
    private void UpdateCurrentJunction(string targetPath)
    {
        try
        {
            var link = _paths.CurrentLink;

            if (Directory.Exists(link) || File.Exists(link))
            {
                var info = new DirectoryInfo(link);
                if (info.LinkTarget is not null)
                {
                    info.Delete();
                }
                else
                {
                    // Something real is sitting where the link belongs; leave it alone.
                    return;
                }
            }

            Directory.CreateSymbolicLink(link, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.LogDebug(ex, "Could not update the 'current' link (this is not fatal).");
        }
    }
}
