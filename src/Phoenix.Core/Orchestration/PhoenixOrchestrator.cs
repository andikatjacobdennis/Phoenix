using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Core.Releases;
using Phoenix.Core.Versioning;

namespace Phoenix.Core.Orchestration;

/// <summary>
/// The whole Phoenix run, in one readable place: check the machine, make sure the
/// prerequisites are there, work out which release should be installed, install it
/// transactionally, start the application, prove it is healthy, and open the browser.
///
/// Every failure path leads either to a working application or to a clear explanation.
/// </summary>
public sealed class PhoenixOrchestrator
{
    private readonly PhoenixOptions _options;
    private readonly PhoenixPaths _paths;
    private readonly PhoenixRunOptions _run;
    private readonly IUserInterface _ui;
    private readonly IEnvironmentService _environment;
    private readonly IReleaseProvider _releases;
    private readonly ReleaseSelector _selector;
    private readonly IReleaseInstaller _installer;
    private readonly IInstallationManager _installations;
    private readonly IPrerequisiteManager _prerequisites;
    private readonly IApplicationLauncher _launcher;
    private readonly IHealthCheckService _health;
    private readonly IBrowserLauncher _browser;
    private readonly ICleanupService _cleanup;
    private readonly ISingleInstanceManager _singleInstance;
    private readonly IStateStore _state;
    private readonly IDiskSpaceService _diskSpace;
    private readonly ILogger<PhoenixOrchestrator> _logger;

    private readonly HashSet<string> _reportedPrerequisites = new(StringComparer.OrdinalIgnoreCase);

    private bool _browserOpened;

    public PhoenixOrchestrator(
        IOptions<PhoenixOptions> options,
        PhoenixPaths paths,
        PhoenixRunOptions run,
        IUserInterface ui,
        IEnvironmentService environment,
        IReleaseProvider releases,
        ReleaseSelector selector,
        IReleaseInstaller installer,
        IInstallationManager installations,
        IPrerequisiteManager prerequisites,
        IApplicationLauncher launcher,
        IHealthCheckService health,
        IBrowserLauncher browser,
        ICleanupService cleanup,
        ISingleInstanceManager singleInstance,
        IStateStore state,
        IDiskSpaceService diskSpace,
        ILogger<PhoenixOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _paths = paths;
        _run = run;
        _ui = ui;
        _environment = environment;
        _releases = releases;
        _selector = selector;
        _installer = installer;
        _installations = installations;
        _prerequisites = prerequisites;
        _launcher = launcher;
        _health = health;
        _browser = browser;
        _cleanup = cleanup;
        _singleInstance = singleInstance;
        _state = state;
        _diskSpace = diskSpace;
        _logger = logger;
    }

    public async Task<ExitCode> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Phoenix run {OperationId} was cancelled.", _run.OperationId);
            _ui.ShowInformation("Cancelled. Nothing was left half-installed.");
            return ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            var error = PhoenixError.Unexpected("PX-UNEXPECTED", ex.Message, ex);
            _logger.LogError(ex, "Unhandled failure during Phoenix run {OperationId}.", _run.OperationId);
            _ui.ShowFailure(error, _run.OperationId, _paths.Logs);
            return ExitCode.UnknownFailure;
        }
    }

    private async Task<ExitCode> ExecuteAsync(CancellationToken cancellationToken)
    {
        var system = _environment.GetSystemInformation();
        _ui.ShowWelcome(system);

        var lockResult = _singleInstance.TryAcquire(_paths.Root);
        if (lockResult.IsFailure)
        {
            _ui.ShowInformation(
                "Phoenix is already running on this computer. Please wait for it to finish, then try again.");
            _logger.LogWarning("Single-instance lock refused: {Reason}", lockResult.Error.TechnicalMessage);
            return ExitCode.AlreadyRunning;
        }

        using var instanceLock = lockResult.Value;

        _paths.EnsureCreated();
        LogEnvironment(system);

        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);

        using (var stage = _ui.BeginStage("Checking your system"))
        {
            var space = _diskSpace.EnsureAvailable(_paths.Root, _options.Security.DiskSpaceMarginBytes);
            if (space.IsFailure)
            {
                stage.Fail();
                _ui.ShowFailure(space.Error, _run.OperationId, _paths.Logs);
                return space.Error.ExitCode;
            }

            stage.Complete();
        }

        ShowSystemDiagnostics(system);

        // ---- Prerequisites the configuration says this product always needs ----
        var prerequisiteExit = await RunPrerequisitesAsync([], cancellationToken).ConfigureAwait(false);
        if (prerequisiteExit is not null)
        {
            return prerequisiteExit.Value;
        }

        var installed = await _installations.GetActiveInstallationAsync(cancellationToken).ConfigureAwait(false);

        if (_run.Rollback)
        {
            return await RunExplicitRollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        var decision = await DecideAsync(installed, state, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Update decision {Action}: {Explanation}",
            decision.Action,
            decision.Explanation);

        switch (decision.Action)
        {
            case UpdateAction.FreshInstall:
            case UpdateAction.Update:
                return await RunInstallAsync(decision, cancellationToken).ConfigureAwait(false);

            case UpdateAction.UpdateAvailableButBlocked:
                if (decision.Target is not null)
                {
                    _ui.ShowInformation(
                        $"A newer version ({decision.Target.Version}) is available. " +
                        "It will be installed when your administrator allows it.");
                }

                return await LaunchExistingAsync(decision.Installed, cancellationToken).ConfigureAwait(false);

            case UpdateAction.UpToDate:
                return await LaunchExistingAsync(decision.Installed, cancellationToken).ConfigureAwait(false);

            case UpdateAction.NoReleaseAvailable:
            default:
                if (decision.Installed is not null)
                {
                    return await LaunchExistingAsync(decision.Installed, cancellationToken).ConfigureAwait(false);
                }

                // Being unable to reach the release source and reaching it to find nothing
                // published are different problems, and the user can only act on one of them.
                var error = decision.SourceUnavailable
                    ? PhoenixError.GitHub(
                        "PX-NO-RELEASE-SOURCE",
                        $"The release source could not be reached and nothing is installed locally. " +
                        $"{decision.Explanation}")
                    : new PhoenixError
                    {
                        Category = ErrorCategory.GitHub,
                        Code = "PX-NO-RELEASE",
                        UserMessage =
                            $"There is nothing to install yet. No {_options.Product.Name} release has been " +
                            "published for this computer.",
                        TechnicalMessage =
                            $"{_options.GitHub.Owner}/{_options.GitHub.Repository} returned no release for " +
                            $"channel {_options.Updates.Channel} carrying a " +
                            $"'{ReleaseManifest.DefaultFileName}' asset, and nothing is installed locally.",
                        ExitCode = ExitCode.InstallationFailure,
                    };

                _ui.ShowFailure(error, _run.OperationId, _paths.Logs);
                return error.ExitCode;
        }
    }

    // ---- Decision -------------------------------------------------------

    private async Task<UpdateDecision> DecideAsync(
        InstallationInfo? installed,
        PhoenixState state,
        CancellationToken cancellationToken)
    {
        if (_options.Updates.Policy == UpdatePolicy.Never && installed is not null && !_run.ForceReinstall)
        {
            return new UpdateDecision
            {
                Action = UpdateAction.UpToDate,
                Installed = installed,
                Explanation = "Update policy is 'Never'; launching the installed version.",
            };
        }

        using var stage = _ui.BeginStage(installed is null ? "Preparing your first install" : "Checking for updates");

        var releases = await _releases.GetReleasesAsync(cancellationToken).ConfigureAwait(false);
        if (releases.IsFailure)
        {
            stage.Warn(installed is null ? "unavailable" : "offline");
            _logger.LogWarning(
                "Release lookup failed: {Code} {Message}",
                releases.Error.Code,
                releases.Error.TechnicalMessage);

            if (installed is not null)
            {
                _ui.ShowInformation("We could not check for updates, so your installed version will be used.");
            }

            return new UpdateDecision
            {
                Action = UpdateAction.NoReleaseAvailable,
                Installed = installed,
                SourceUnavailable = true,
                Explanation = releases.Error.TechnicalMessage,
            };
        }

        var candidates = _selector.SelectCandidates(
            releases.Value,
            _options.Updates,
            state.RejectedVersions);

        stage.Complete();

        if (_run.Diagnostics)
        {
            _ui.ShowDiagnostics(
                "Release candidates",
                candidates
                    .Select(c => new KeyValuePair<string, string>(
                        c.Release.Tag,
                        $"{ChannelResolver.FromVersion(c.Version).DisplayName()}, published " +
                        $"{c.Release.PublishedAt?.ToString("u") ?? "unknown"}"))
                    .ToList());
        }

        var decision = _selector.Decide(installed, candidates, _options.Updates);

        if (_run.CheckOnly && decision.Action is UpdateAction.Update)
        {
            return decision with
            {
                Action = UpdateAction.UpdateAvailableButBlocked,
                Explanation = "--check-only was requested.",
            };
        }

        if (_run.ForceReinstall && decision.Action == UpdateAction.UpToDate && candidates.Count > 0)
        {
            return decision with
            {
                Action = UpdateAction.Update,
                Candidates = candidates,
                Explanation = "--force-reinstall was requested.",
            };
        }

        return decision;
    }

    // ---- Install --------------------------------------------------------

    private async Task<ExitCode> RunInstallAsync(UpdateDecision decision, CancellationToken cancellationToken)
    {
        if (decision.Action == UpdateAction.Update && decision.Target is not null)
        {
            _ui.ShowUpdateAvailable(decision.Installed?.Version, decision.Target.Version);

            if (_options.Updates.ConfirmBeforeUpdate &&
                !_run.AssumeYes &&
                !_ui.Confirm("Install this update now?", defaultValue: true))
            {
                _ui.ShowInformation("Update skipped.");
                return await LaunchExistingAsync(decision.Installed, cancellationToken).ConfigureAwait(false);
            }
        }

        PhoenixError? lastError = null;
        SemanticVersion? lastAttemptedVersion = null;

        foreach (var candidate in decision.Candidates)
        {
            lastAttemptedVersion = candidate.Version;
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("Attempting release {Tag} ({Version}).", candidate.Release.Tag, candidate.Version);

            var attempt = await TryInstallCandidateAsync(candidate, decision.Installed, cancellationToken)
                .ConfigureAwait(false);

            if (attempt.IsSuccess)
            {
                return ExitCode.Success;
            }

            lastError = attempt.Error;

            await _installations
                .RejectVersionAsync(candidate.Version, attempt.Error.Code, cancellationToken)
                .ConfigureAwait(false);

            if (!attempt.Error.AllowsRemoteFallback)
            {
                break;
            }

            if (!ReferenceEquals(candidate, decision.Candidates[^1]))
            {
                _ui.ShowInformation("Trying the previous available version...");
            }
        }

        return await RecoverAsync(lastError, lastAttemptedVersion, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> TryInstallCandidateAsync(
        ReleaseCandidate candidate,
        InstallationInfo? previous,
        CancellationToken cancellationToken)
    {
        var resolved = await _installer.ResolveAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            _logger.LogWarning(
                "Release {Tag} rejected during manifest validation: {Code} {Message}",
                candidate.Release.Tag,
                resolved.Error.Code,
                resolved.Error.TechnicalMessage);
            return resolved.ToResult();
        }

        // The release itself can declare prerequisites; they are checked before anything
        // large is downloaded.
        var prerequisiteExit = await RunPrerequisitesAsync(
            resolved.Value.Manifest.Prerequisites,
            cancellationToken).ConfigureAwait(false);

        if (prerequisiteExit is not null)
        {
            return PhoenixError.Prerequisite(
                "PX-PREREQ-BLOCKED",
                $"Release {candidate.Version} cannot be installed until its prerequisites are satisfied.");
        }

        var installation = await _ui.RunWithProgressAsync(
            "Downloading",
            (progress, token) => _installer.InstallAsync(resolved.Value, progress, token),
            cancellationToken).ConfigureAwait(false);

        if (installation.IsFailure)
        {
            _logger.LogWarning(
                "Installing {Version} failed: {Code} {Message}",
                candidate.Version,
                installation.Error.Code,
                installation.Error.TechnicalMessage);
            _ui.ShowWarning(installation.Error.UserMessage);
            return installation.ToResult();
        }

        using (var stage = _ui.BeginStage("Installing"))
        {
            // Stop whatever is running out of the old directory before switching, so the
            // previous version never keeps a half-replaced installation alive.
            if (previous is not null)
            {
                await _launcher.StopRunningInstancesAsync(previous.Path, cancellationToken).ConfigureAwait(false);
            }

            // Activation is the point of no return, so it is not cancellable: it either
            // completes or leaves the previous version active.
            var activate = await _installations
                .ActivateAsync(installation.Value, CancellationToken.None)
                .ConfigureAwait(false);

            if (activate.IsFailure)
            {
                stage.Fail();
                return activate;
            }

            stage.Complete();
        }

        var started = await StartAndVerifyAsync(installation.Value, cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            return started;
        }

        await _installations.MarkKnownGoodAsync(installation.Value, cancellationToken).ConfigureAwait(false);
        await SafeCleanupAsync(cancellationToken).ConfigureAwait(false);

        _ui.ShowSuccess(
            previous is null ? "Ready" : "Updated successfully",
            previous is null ? null : $"{previous.Version} to {installation.Value.Version}");

        OpenBrowser();
        _ui.ShowReady(_options.Application.Name, HealthEndpointResolver.ResolveApplicationUrl(_options), _browserOpened);
        return Result.Success();
    }

    // ---- Launch and health ----------------------------------------------

    private async Task<Result> StartAndVerifyAsync(InstallationInfo installation, CancellationToken cancellationToken)
    {
        if (_run.NoLaunch)
        {
            _ui.ShowInformation("Installed. The application was not started because --no-launch was requested.");
            return Result.Success();
        }

        IApplicationProcess? process = null;

        using (var stage = _ui.BeginStage("Starting"))
        {
            if (_options.Application.ReuseRunningInstance)
            {
                process = _launcher.FindRunningInstance(installation);
                if (process is not null)
                {
                    _logger.LogInformation(
                        "Reusing running instance {ProcessId} of {Version}.",
                        process.ProcessId,
                        installation.Version);
                }
            }

            if (process is null)
            {
                var start = await _launcher.StartAsync(installation, cancellationToken).ConfigureAwait(false);
                if (start.IsFailure)
                {
                    stage.Fail();
                    return start.ToResult();
                }

                process = start.Value;
            }

            stage.Complete();
        }

        if (!_options.HealthCheck.Enabled)
        {
            _logger.LogWarning("Health checking is disabled; {Version} is accepted without verification.", installation.Version);
            return Result.Success();
        }

        var healthUrl = HealthEndpointResolver.Resolve(_options, installation);

        using var stage2 = _ui.BeginStage("Checking the application");
        var health = await _health.WaitForHealthyAsync(healthUrl, process, cancellationToken).ConfigureAwait(false);

        if (health.IsFailure)
        {
            stage2.Fail();
            _logger.LogWarning(
                "Health check for {Version} failed: {Message}",
                installation.Version,
                health.Error.TechnicalMessage);

            await process.StopAsync(cancellationToken).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            return health;
        }

        stage2.Complete();
        return Result.Success();
    }

    private async Task<ExitCode> LaunchExistingAsync(InstallationInfo? installation, CancellationToken cancellationToken)
    {
        if (installation is null)
        {
            var error = PhoenixError.Installation(
                "PX-NOTHING-INSTALLED",
                "There is no installed version to launch.",
                requiresRollback: false);
            _ui.ShowFailure(error, _run.OperationId, _paths.Logs);
            return error.ExitCode;
        }

        var started = await StartAndVerifyAsync(installation, cancellationToken).ConfigureAwait(false);

        if (started.IsSuccess)
        {
            if (!installation.IsKnownGood)
            {
                await _installations.MarkKnownGoodAsync(installation, cancellationToken).ConfigureAwait(false);
            }

            await SafeCleanupAsync(cancellationToken).ConfigureAwait(false);
            OpenBrowser();
            _ui.ShowReady(_options.Application.Name, HealthEndpointResolver.ResolveApplicationUrl(_options), _browserOpened);
            return ExitCode.Success;
        }

        // The installed version itself is broken: fall back to the known-good copy.
        return await RecoverAsync(started.Error, installation.Version, cancellationToken).ConfigureAwait(false);
    }

    // ---- Recovery -------------------------------------------------------

    private async Task<ExitCode> RunExplicitRollbackAsync(CancellationToken cancellationToken)
    {
        _ui.ShowInformation("Restoring your previous version...");
        var recovered = await RestoreKnownGoodAsync(null, "Requested with --rollback", cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure)
        {
            _ui.ShowFailure(recovered.Error, _run.OperationId, _paths.Logs);
            return recovered.Error.ExitCode;
        }

        _ui.ShowSuccess("Your application is ready.", $"Restored {recovered.Value.Version}.");
        OpenBrowser();
        _ui.ShowReady(_options.Application.Name, HealthEndpointResolver.ResolveApplicationUrl(_options), _browserOpened);
        return ExitCode.Success;
    }

    /// <param name="failedVersion">
    /// The version that just failed, so it is excluded from the recovery candidates and
    /// recorded as the reason the machine had to roll back.
    /// </param>
    private async Task<ExitCode> RecoverAsync(
        PhoenixError? failure,
        SemanticVersion? failedVersion,
        CancellationToken cancellationToken)
    {
        failure ??= PhoenixError.Installation("PX-INSTALL-EXHAUSTED", "No release could be installed.");

        if (!_options.Recovery.EnableAutomaticRollback)
        {
            _ui.ShowFailure(failure, _run.OperationId, _paths.Logs);
            return failure.ExitCode;
        }

        // Nothing has ever worked on this machine, so there is nothing to restore. That is an
        // installation failure, not a recovery failure: the computer is exactly as it was.
        var knownGood = await _installations.GetKnownGoodInstallationAsync(cancellationToken).ConfigureAwait(false);
        if (knownGood is null && _installations.ListInstalledVersions().Count == 0)
        {
            _logger.LogError(
                "Installation failed with {Code} and there is no previous version to restore.",
                failure.Code);

            _ui.ShowFailure(failure, _run.OperationId, _paths.Logs);
            return failure.ExitCode;
        }

        _ui.ShowInformation("Restoring your previous version...");

        var recovered = await RestoreKnownGoodAsync(failedVersion, failure.Code, cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure)
        {
            _logger.LogError(
                "Recovery failed after {Code}: {Message}",
                failure.Code,
                recovered.Error.TechnicalMessage);

            _ui.ShowFailure(failure, _run.OperationId, _paths.Logs);
            return ExitCode.RecoveryFailure;
        }

        _ui.ShowFailure(
            failure,
            _run.OperationId,
            _paths.Logs,
            recoveryNote: "The update was not applied, but your previous working version has been restored.");

        OpenBrowser();
        _ui.ShowReady(_options.Application.Name, HealthEndpointResolver.ResolveApplicationUrl(_options), _browserOpened);
        return failure.ExitCode;
    }

    private async Task<Result<InstallationInfo>> RestoreKnownGoodAsync(
        SemanticVersion? failedVersion,
        string reason,
        CancellationToken cancellationToken)
    {
        var rollback = await _installations.RollbackAsync(failedVersion, reason, cancellationToken)
            .ConfigureAwait(false);

        if (rollback.IsFailure)
        {
            return rollback;
        }

        using (var stage = _ui.BeginStage("Restoring"))
        {
            stage.Complete($"{rollback.Value.Version}");
        }

        var started = await StartAndVerifyAsync(rollback.Value, cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            return PhoenixError.Recovery(
                "PX-RECOVERY-UNHEALTHY",
                $"The known-good version {rollback.Value.Version} could not be started: " +
                started.Error.TechnicalMessage,
                started.Error.Exception);
        }

        return rollback.Value;
    }

    // ---- Prerequisites ---------------------------------------------------

    /// <summary>Returns an exit code when the run must stop, or null to carry on.</summary>
    private async Task<ExitCode?> RunPrerequisitesAsync(
        IReadOnlyList<ManifestPrerequisite> releaseRequirements,
        CancellationToken cancellationToken)
    {
        if (!_options.Prerequisites.Enabled)
        {
            return null;
        }

        var result = await _prerequisites.EnsureAsync(releaseRequirements, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            _ui.ShowFailure(result.Error, _run.OperationId, _paths.Logs);
            return result.Error.ExitCode;
        }

        var report = result.Value;

        // The prerequisite phase runs twice - once for what configuration always requires, and
        // again for what the chosen release declares. Only show the table when it says
        // something new, rather than repeating an identical list.
        if (report.Items.Any(i => _reportedPrerequisites.Add(i.Id)))
        {
            _ui.ShowPrerequisites(report);
        }

        if (report.RebootRequired)
        {
            await _state.UpdateAsync(
                s => s with
                {
                    PendingReboot = true,
                    PendingOperation = new PendingOperation
                    {
                        Kind = PendingOperationKind.AwaitingReboot,
                        OperationId = _run.OperationId,
                    },
                },
                cancellationToken).ConfigureAwait(false);

            _ui.ShowRebootRequired(
                report.Items
                    .Where(i => i.State == PrerequisiteState.RebootRequired)
                    .Select(i => i.DisplayName)
                    .ToList());

            return ExitCode.RebootRequired;
        }

        if (report.HasMandatoryFailure)
        {
            var failed = report.Items.First(i => i.IsMandatory && i.State == PrerequisiteState.Failed);
            var error = PhoenixError.Prerequisite(
                "PX-PREREQ-FAILED",
                $"Mandatory prerequisite '{failed.Id}' could not be satisfied: {failed.Detail}");

            _ui.ShowFailure(error, _run.OperationId, _paths.Logs);
            return error.ExitCode;
        }

        if (report.AnyInstalled)
        {
            await _state.UpdateAsync(
                s => s with { PendingReboot = false, PendingOperation = null },
                cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // ---- Helpers ---------------------------------------------------------

    private async Task<PhoenixState> LoadStateAsync(CancellationToken cancellationToken)
    {
        var state = await _state.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (_state.LastLoadWasRecovered)
        {
            _logger.LogWarning("Phoenix state was unreadable; rebuilding it from the filesystem.");
            state = await _installations.RepairStateAsync(cancellationToken).ConfigureAwait(false);
        }

        return state;
    }

    private void OpenBrowser()
    {
        if (_run.NoBrowser || !_options.Browser.LaunchOnSuccess || !_options.Testing.AllowBrowserLaunch)
        {
            return;
        }

        if (_browserOpened)
        {
            return;
        }

        var url = HealthEndpointResolver.ResolveApplicationUrl(_options);
        var opened = _browser.Open(url);

        if (opened.IsFailure)
        {
            _logger.LogWarning("Browser launch failed: {Message}", opened.Error.TechnicalMessage);
            return;
        }

        _browserOpened = true;
    }

    private async Task SafeCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _cleanup.CleanupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cleanup is housekeeping. A successful installation is not thrown away because
            // a stale folder could not be deleted.
            _logger.LogWarning(ex, "Cleanup did not complete.");
        }
    }

    private void LogEnvironment(SystemInformation system)
    {
        _logger.LogInformation(
            "Phoenix {PhoenixVersion} starting in {Environment} for {Product} on {Machine} ({Os}/{Arch}), elevated={Elevated}, root={Root}",
            system.PhoenixVersion,
            _environment.EnvironmentName,
            _options.Product.Name,
            system.MachineName,
            system.OperatingSystem,
            system.Architecture,
            system.IsElevated,
            _paths.Root);
    }

    private void ShowSystemDiagnostics(SystemInformation system)
    {
        if (!_run.Diagnostics)
        {
            return;
        }

        _ui.ShowDiagnostics(
            "Environment",
            [
                new("Phoenix", system.PhoenixVersion),
                new("Environment", _environment.EnvironmentName),
                new("Channel", _options.Updates.Channel.DisplayName()),
                new("Update policy", _options.Updates.Policy.ToString()),
                new("Machine", $"{system.MachineName} ({system.OperatingSystem}, {system.Architecture})"),
                new("Elevated", system.IsElevated ? "yes" : "no"),
                new("Repository", $"{_options.GitHub.Owner}/{_options.GitHub.Repository}"),
                new("Install root", _paths.Root),
                new("Logs", _paths.Logs),
                new("Application URL", _options.Application.Url),
                new("Reference", _run.OperationId),
            ]);
    }
}
