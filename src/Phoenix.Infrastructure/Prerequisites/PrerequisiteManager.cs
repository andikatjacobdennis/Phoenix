using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Core.Versioning;

namespace Phoenix.Infrastructure.Prerequisites;

/// <summary>
/// Makes sure the frameworks the application needs are present, in the right versions.
///
/// The order is always detect, then decide, then install - never install blindly. Detection is
/// read-only and runs concurrently; installation mutates the machine and runs one at a time.
/// A newer compatible version is left alone rather than downgraded.
/// </summary>
public sealed class PrerequisiteManager : IPrerequisiteManager
{
    private readonly PhoenixOptions _options;
    private readonly IReadOnlyDictionary<DetectionStrategy, IPrerequisiteDetector> _detectors;
    private readonly PrerequisiteInstaller _installer;
    private readonly IEnvironmentService _environment;
    private readonly IStateStore _state;
    private readonly ILogger<PrerequisiteManager> _logger;

    private readonly Dictionary<string, PrerequisiteStatus> _completed = new(StringComparer.OrdinalIgnoreCase);

    public PrerequisiteManager(
        IOptions<PhoenixOptions> options,
        IEnumerable<IPrerequisiteDetector> detectors,
        PrerequisiteInstaller installer,
        IEnvironmentService environment,
        IStateStore state,
        ILogger<PrerequisiteManager> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(detectors);

        _options = options.Value;
        _installer = installer;
        _environment = environment;
        _state = state;
        _logger = logger;

        // A later registration wins, so a company can replace a built-in detector.
        var map = new Dictionary<DetectionStrategy, IPrerequisiteDetector>();
        foreach (var detector in detectors)
        {
            map[detector.Strategy] = detector;
        }

        _detectors = map;
    }

    public async Task<Result<PrerequisiteReport>> EnsureAsync(
        IReadOnlyList<ManifestPrerequisite> releaseRequirements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(releaseRequirements);

        var definitions = SelectApplicable(releaseRequirements);
        if (definitions.Count == 0)
        {
            return new PrerequisiteReport { Items = [.. _completed.Values] };
        }

        // Detection touches nothing, so several probes can run at once.
        var detections = await Task.WhenAll(
                definitions.Select(d => EvaluateAsync(d, cancellationToken)))
            .ConfigureAwait(false);

        var statuses = new List<PrerequisiteStatus>();

        foreach (var (definition, status) in detections)
        {
            if (status.State != PrerequisiteState.Missing || !_options.Prerequisites.InstallMissing)
            {
                statuses.Add(status);
                _completed[definition.Id] = status;
                continue;
            }

            var installed = await InstallAndConfirmAsync(definition, cancellationToken).ConfigureAwait(false);
            statuses.Add(installed);
            _completed[definition.Id] = installed;
        }

        await RecordSatisfiedAsync(statuses, cancellationToken).ConfigureAwait(false);

        var unknown = releaseRequirements
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .Where(r => !_options.Prerequisites.Required.Any(d =>
                string.Equals(d.Id, r.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var requirement in unknown)
        {
            // The release wants something this installation does not know how to check.
            // Report it rather than pretending everything is fine.
            _logger.LogWarning(
                "Release requires prerequisite '{Id}', which is not defined in configuration.",
                requirement.Id);

            statuses.Add(new PrerequisiteStatus
            {
                Id = requirement.Id,
                DisplayName = requirement.Id,
                State = PrerequisiteState.Skipped,
                IsMandatory = false,
                Detail = "Required by the release but not defined in Phoenix configuration.",
            });
        }

        return new PrerequisiteReport { Items = MergeWithCompleted(statuses) };
    }

    private IReadOnlyList<PrerequisiteStatus> MergeWithCompleted(List<PrerequisiteStatus> current)
    {
        var byId = new Dictionary<string, PrerequisiteStatus>(StringComparer.OrdinalIgnoreCase);

        foreach (var status in _completed.Values)
        {
            byId[status.Id] = status;
        }

        foreach (var status in current)
        {
            byId[status.Id] = status;
        }

        return byId.Values
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<PrerequisiteDefinition> SelectApplicable(IReadOnlyList<ManifestPrerequisite> releaseRequirements)
    {
        var architecture = _environment.GetSystemInformation().Architecture;
        var result = new List<PrerequisiteDefinition>();

        foreach (var definition in _options.Prerequisites.Required)
        {
            // Already handled earlier in this run.
            if (_completed.ContainsKey(definition.Id))
            {
                continue;
            }

            if (definition.ApplicableEnvironments.Length > 0 &&
                !definition.ApplicableEnvironments.Contains(
                    _environment.EnvironmentName,
                    StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(definition.Architecture, "any", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(definition.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(definition);
        }

        // A release can tighten a minimum version; it can never introduce an unknown component.
        foreach (var requirement in releaseRequirements)
        {
            var definition = result.FirstOrDefault(d =>
                string.Equals(d.Id, requirement.Id, StringComparison.OrdinalIgnoreCase));

            if (definition is null || string.IsNullOrWhiteSpace(requirement.MinimumVersion))
            {
                continue;
            }

            if (SemanticVersion.TryParse(requirement.MinimumVersion, out var required) &&
                (!SemanticVersion.TryParse(definition.MinimumVersion, out var configured) || required > configured))
            {
                _logger.LogInformation(
                    "Release raises the minimum version of {Id} to {Version}.",
                    definition.Id,
                    required);

                definition.MinimumVersion = required.ToString();
            }
        }

        return result;
    }

    private async Task<(PrerequisiteDefinition Definition, PrerequisiteStatus Status)> EvaluateAsync(
        PrerequisiteDefinition definition,
        CancellationToken cancellationToken)
    {
        if (!_detectors.TryGetValue(definition.Detection.Strategy, out var detector))
        {
            return (definition, new PrerequisiteStatus
            {
                Id = definition.Id,
                DisplayName = definition.DisplayName,
                State = PrerequisiteState.Failed,
                IsMandatory = definition.Mandatory,
                Detail = $"No detector is registered for strategy '{definition.Detection.Strategy}'.",
            });
        }

        if (!VersionRange.TryCreate(
                definition.RequiredVersion,
                definition.MinimumVersion,
                definition.MaximumVersion,
                definition.VersionRange,
                out var range))
        {
            return (definition, new PrerequisiteStatus
            {
                Id = definition.Id,
                DisplayName = definition.DisplayName,
                State = PrerequisiteState.Failed,
                IsMandatory = definition.Mandatory,
                Detail = "The configured version requirement could not be parsed.",
            });
        }

        DetectionResult detection;

        try
        {
            detection = await detector.DetectAsync(definition, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Detection of {Id} threw; treating it as missing.", definition.Id);
            detection = DetectionResult.NotFound(ex.Message);
        }

        var satisfied = detection.AllDetectedVersions.Count > 0
            ? detection.AllDetectedVersions.Where(range.Satisfies).OrderByDescending(v => v).FirstOrDefault()
            : (range.Satisfies(detection.DetectedVersion) ? detection.DetectedVersion : null);

        _logger.LogInformation(
            "Prerequisite {Id}: installed={Installed}, detected={Detected}, required={Required}, satisfied={Satisfied}.",
            definition.Id,
            detection.IsInstalled,
            detection.DetectedVersion?.ToString() ?? "none",
            range.ToString(),
            satisfied?.ToString() ?? "no");

        if (satisfied is not null)
        {
            return (definition, new PrerequisiteStatus
            {
                Id = definition.Id,
                DisplayName = definition.DisplayName,
                State = PrerequisiteState.Satisfied,
                DetectedVersion = satisfied,
                RequirementDescription = range.ToString(),
                IsMandatory = definition.Mandatory,
                Detail = detection.Evidence,
            });
        }

        return (definition, new PrerequisiteStatus
        {
            Id = definition.Id,
            DisplayName = definition.DisplayName,
            State = PrerequisiteState.Missing,
            DetectedVersion = detection.DetectedVersion,
            RequirementDescription = range.ToString(),
            IsMandatory = definition.Mandatory,
            Detail = detection.IsInstalled
                ? $"Found {detection.DetectedVersion}, which does not satisfy {range}."
                : detection.Evidence,
        });
    }

    private async Task<PrerequisiteStatus> InstallAndConfirmAsync(
        PrerequisiteDefinition definition,
        CancellationToken cancellationToken)
    {
        if (definition.Installer is null)
        {
            return new PrerequisiteStatus
            {
                Id = definition.Id,
                DisplayName = definition.DisplayName,
                State = definition.Mandatory ? PrerequisiteState.Failed : PrerequisiteState.Skipped,
                IsMandatory = definition.Mandatory,
                Detail = "Not installed, and no installer is configured for it.",
            };
        }

        var install = await _installer.InstallAsync(definition, cancellationToken).ConfigureAwait(false);

        if (install.IsFailure)
        {
            return new PrerequisiteStatus
            {
                Id = definition.Id,
                DisplayName = definition.DisplayName,
                State = definition.Mandatory ? PrerequisiteState.Failed : PrerequisiteState.Skipped,
                IsMandatory = definition.Mandatory,
                Detail = install.Error.TechnicalMessage,
            };
        }

        if (install.Value.RebootRequired)
        {
            return new PrerequisiteStatus
            {
                Id = definition.Id,
                DisplayName = definition.DisplayName,
                State = PrerequisiteState.RebootRequired,
                IsMandatory = definition.Mandatory,
                Detail = install.Value.Detail,
            };
        }

        // Confirm rather than trust: an installer that reports success has still been known
        // to leave nothing behind.
        var (_, confirmed) = await EvaluateAsync(definition, cancellationToken).ConfigureAwait(false);

        if (confirmed.State == PrerequisiteState.Satisfied)
        {
            return confirmed with
            {
                State = PrerequisiteState.Installed,
                Detail = install.Value.Detail,
            };
        }

        if (_options.Testing.SimulatePrerequisiteInstalls)
        {
            // In simulation the component genuinely was not installed; say so plainly.
            return confirmed with
            {
                State = PrerequisiteState.Installed,
                Detail = "Simulated installation; detection intentionally still reports it missing.",
            };
        }

        return new PrerequisiteStatus
        {
            Id = definition.Id,
            DisplayName = definition.DisplayName,
            State = definition.Mandatory ? PrerequisiteState.Failed : PrerequisiteState.Skipped,
            IsMandatory = definition.Mandatory,
            Detail = $"The installer finished ({install.Value.Detail}) but the component is still not detected.",
        };
    }

    private async Task RecordSatisfiedAsync(
        IReadOnlyList<PrerequisiteStatus> statuses,
        CancellationToken cancellationToken)
    {
        var satisfied = statuses
            .Where(s => s.State is PrerequisiteState.Satisfied or PrerequisiteState.Installed)
            .Select(s => new CompletedPrerequisite
            {
                Id = s.Id,
                DetectedVersion = s.DetectedVersion?.ToString(),
                InstalledAt = DateTimeOffset.UtcNow,
            })
            .ToList();

        if (satisfied.Count == 0)
        {
            return;
        }

        await _state.UpdateAsync(
            state =>
            {
                var merged = state.Prerequisites
                    .Where(p => !satisfied.Any(s => string.Equals(s.Id, p.Id, StringComparison.OrdinalIgnoreCase)))
                    .Concat(satisfied)
                    .ToList();

                return state with { Prerequisites = merged };
            },
            cancellationToken).ConfigureAwait(false);
    }
}
