using System.Globalization;
using Microsoft.Extensions.Options;
using Phoenix.Core.Versioning;

namespace Phoenix.Core.Configuration;

/// <summary>
/// Fails the run immediately when configuration cannot possibly work. Phoenix would rather
/// print one clear sentence at startup than discover a typo halfway through an update.
/// </summary>
public sealed class PhoenixOptionsValidator : IValidateOptions<PhoenixOptions>
{
    private readonly string _environmentName;

    public PhoenixOptionsValidator(string environmentName) => _environmentName = environmentName;

    public ValidateOptionsResult Validate(string? name, PhoenixOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        Require(failures, options.Company.Name, "Phoenix:Company:Name");
        Require(failures, options.Product.Name, "Phoenix:Product:Name");
        Require(failures, options.Application.Id, "Phoenix:Application:Id");
        Require(failures, options.Application.Name, "Phoenix:Application:Name");
        Require(failures, options.GitHub.Owner, "Phoenix:GitHub:Owner");
        Require(failures, options.GitHub.Repository, "Phoenix:GitHub:Repository");
        Require(failures, options.Directories.Root, "Phoenix:Directories:Root");

        ValidateAbsoluteUrl(failures, options.Application.Url, "Phoenix:Application:Url");
        ValidateAbsoluteUrl(failures, options.GitHub.ApiBaseUrl, "Phoenix:GitHub:ApiBaseUrl");

        if (!string.IsNullOrWhiteSpace(options.Browser.Url))
        {
            ValidateAbsoluteUrl(failures, options.Browser.Url, "Phoenix:Browser:Url");
        }

        if (!string.IsNullOrWhiteSpace(options.HealthCheck.Url))
        {
            ValidateAbsoluteUrl(failures, options.HealthCheck.Url, "Phoenix:HealthCheck:Url");
        }
        else if (options.HealthCheck.Enabled && !options.HealthCheck.Path.StartsWith('/'))
        {
            failures.Add("Phoenix:HealthCheck:Path must start with '/'.");
        }

        RequirePositive(failures, options.Network.RequestTimeoutSeconds, "Phoenix:Network:RequestTimeoutSeconds");
        RequirePositive(failures, options.Network.DownloadTimeoutMinutes, "Phoenix:Network:DownloadTimeoutMinutes");
        RequireNonNegative(failures, options.Network.MaxRetries, "Phoenix:Network:MaxRetries");
        RequirePositive(failures, options.HealthCheck.RequestTimeoutSeconds, "Phoenix:HealthCheck:RequestTimeoutSeconds");
        RequirePositive(failures, options.HealthCheck.MaxAttempts, "Phoenix:HealthCheck:MaxAttempts");
        RequirePositive(failures, options.HealthCheck.OverallTimeoutSeconds, "Phoenix:HealthCheck:OverallTimeoutSeconds");
        RequireNonNegative(failures, options.Updates.RemoteFallbackCount, "Phoenix:Updates:RemoteFallbackCount");
        RequirePositive(failures, options.Recovery.KeepVersions, "Phoenix:Recovery:KeepVersions");
        RequirePositive(failures, options.Recovery.BackupRetention, "Phoenix:Recovery:BackupRetention");
        RequirePositive(failures, options.Application.ShutdownTimeoutSeconds, "Phoenix:Application:ShutdownTimeoutSeconds");
        RequirePositive(failures, options.GitHub.MaxReleasesToInspect, "Phoenix:GitHub:MaxReleasesToInspect");

        if (options.Security.MaxPackageSizeBytes <= 0)
        {
            failures.Add("Phoenix:Security:MaxPackageSizeBytes must be greater than zero.");
        }

        if (options.Security.MaxExtractedSizeBytes < options.Security.MaxPackageSizeBytes)
        {
            failures.Add("Phoenix:Security:MaxExtractedSizeBytes must be at least MaxPackageSizeBytes.");
        }

        if (options.Updates.Policy == UpdatePolicy.Pinned)
        {
            if (string.IsNullOrWhiteSpace(options.Updates.PinnedVersion))
            {
                failures.Add("Phoenix:Updates:PinnedVersion is required when Policy is 'Pinned'.");
            }
            else if (!SemanticVersion.TryParse(options.Updates.PinnedVersion, out _))
            {
                failures.Add($"Phoenix:Updates:PinnedVersion '{options.Updates.PinnedVersion}' is not a valid semantic version.");
            }
        }

        ValidatePrerequisites(options, failures);
        ValidateTesting(options, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private void ValidateTesting(PhoenixOptions options, List<string> failures)
    {
        var isDevelopment = string.Equals(_environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        if (!isDevelopment && options.Testing.SimulateFault != SimulatedFault.None)
        {
            failures.Add(
                $"Phoenix:Testing:SimulateFault is '{options.Testing.SimulateFault}' but the environment is " +
                $"'{_environmentName}'. Fault injection is only permitted in Development.");
        }

        if (!isDevelopment && options.Testing.SimulatePrerequisiteInstalls)
        {
            failures.Add(
                "Phoenix:Testing:SimulatePrerequisiteInstalls is only permitted in Development.");
        }
    }

    private void ValidatePrerequisites(PhoenixOptions options, List<string> failures)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prerequisite in options.Prerequisites.Required)
        {
            var label = string.IsNullOrWhiteSpace(prerequisite.Id) ? "<missing id>" : prerequisite.Id;

            if (string.IsNullOrWhiteSpace(prerequisite.Id))
            {
                failures.Add("Every entry in Phoenix:Prerequisites:Required needs an Id.");
                continue;
            }

            if (!seen.Add(prerequisite.Id))
            {
                failures.Add($"Prerequisite id '{prerequisite.Id}' is defined more than once.");
            }

            if (!VersionRange.TryCreate(
                    prerequisite.RequiredVersion,
                    prerequisite.MinimumVersion,
                    prerequisite.MaximumVersion,
                    prerequisite.VersionRange,
                    out _))
            {
                failures.Add($"Prerequisite '{label}' has an invalid version requirement.");
            }

            ValidateDetection(prerequisite, label, failures);
            ValidateInstaller(options, prerequisite, label, failures);
        }
    }

    private static void ValidateDetection(PrerequisiteDefinition prerequisite, string label, List<string> failures)
    {
        var detection = prerequisite.Detection;

        switch (detection.Strategy)
        {
            case DetectionStrategy.DotNetRuntime when string.IsNullOrWhiteSpace(detection.RuntimeName):
                failures.Add($"Prerequisite '{label}' uses DotNetRuntime detection and needs Detection:RuntimeName.");
                break;

            case DetectionStrategy.ExecutableVersion or DetectionStrategy.FileVersion
                when string.IsNullOrWhiteSpace(detection.Path):
                failures.Add($"Prerequisite '{label}' uses {detection.Strategy} detection and needs Detection:Path.");
                break;

            case DetectionStrategy.Registry
                when string.IsNullOrWhiteSpace(detection.RegistryKey) || string.IsNullOrWhiteSpace(detection.RegistryValue):
                failures.Add($"Prerequisite '{label}' uses Registry detection and needs Detection:RegistryKey and Detection:RegistryValue.");
                break;

            case DetectionStrategy.Command when string.IsNullOrWhiteSpace(detection.Path):
                failures.Add($"Prerequisite '{label}' uses Command detection and needs Detection:Path.");
                break;

            default:
                break;
        }

        if (detection.Strategy == DetectionStrategy.Registry &&
            !string.IsNullOrWhiteSpace(detection.RegistryHive) &&
            detection.RegistryHive is not ("HKLM" or "HKCU" or "hklm" or "hkcu"))
        {
            failures.Add($"Prerequisite '{label}' has an unsupported Detection:RegistryHive (use HKLM or HKCU).");
        }
    }

    private static void ValidateInstaller(
        PhoenixOptions options,
        PrerequisiteDefinition prerequisite,
        string label,
        List<string> failures)
    {
        var installer = prerequisite.Installer;
        if (installer is null)
        {
            if (prerequisite.Mandatory && options.Prerequisites.InstallMissing &&
                prerequisite.Detection.Strategy != DetectionStrategy.AssumePresent)
            {
                // Not fatal: some prerequisites are deliberately "detect and tell the user".
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(installer.Url) && string.IsNullOrWhiteSpace(installer.LocalPath))
        {
            failures.Add($"Prerequisite '{label}' has an Installer section without a Url or LocalPath.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(installer.Url))
        {
            if (!Uri.TryCreate(installer.Url, UriKind.Absolute, out var uri))
            {
                failures.Add($"Prerequisite '{label}' has an invalid Installer:Url.");
            }
            else if (options.Security.RequireHttps && uri.Scheme != Uri.UriSchemeHttps)
            {
                failures.Add($"Prerequisite '{label}' has a non-HTTPS Installer:Url, which Security:RequireHttps forbids.");
            }

            if (options.Security.RequireChecksum && !IsSha256(installer.Sha256))
            {
                failures.Add($"Prerequisite '{label}' needs a 64-character hex Installer:Sha256 when downloading an installer.");
            }
        }

        if (installer.AcceptedExitCodes.Length == 0)
        {
            failures.Add($"Prerequisite '{label}' needs at least one Installer:AcceptedExitCodes entry.");
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static void Require(List<string> failures, string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{key} is required.");
        }
    }

    private static void RequirePositive(List<string> failures, int value, string key)
    {
        if (value <= 0)
        {
            failures.Add($"{key} must be greater than zero (was {value.ToString(CultureInfo.InvariantCulture)}).");
        }
    }

    private static void RequireNonNegative(List<string> failures, int value, string key)
    {
        if (value < 0)
        {
            failures.Add($"{key} cannot be negative (was {value.ToString(CultureInfo.InvariantCulture)}).");
        }
    }

    private static void ValidateAbsoluteUrl(List<string> failures, string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{key} is required.");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{key} must be an absolute http or https URL (was '{value}').");
        }
    }
}
