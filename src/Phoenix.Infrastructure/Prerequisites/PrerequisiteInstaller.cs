using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Errors;
using Phoenix.Infrastructure.Processes;
using Phoenix.Infrastructure.Prerequisites.Detectors;

namespace Phoenix.Infrastructure.Prerequisites;

public sealed record PrerequisiteInstallOutcome(bool RebootRequired, int ExitCode, string Detail);

/// <summary>
/// Downloads and runs a prerequisite installer.
///
/// Two safety rules shape this class: an installer is verified against its SHA-256 before it
/// is ever executed, and elevation is requested for the installer alone rather than for
/// Phoenix as a whole. In automated tests, <c>Testing:SimulatePrerequisiteInstalls</c> makes
/// the whole thing a no-op so a test run can never change machine-wide software.
/// </summary>
public sealed class PrerequisiteInstaller
{
    private readonly PhoenixOptions _options;
    private readonly PhoenixPaths _paths;
    private readonly IDownloadManager _downloads;
    private readonly IPackageVerifier _verifier;
    private readonly IEnvironmentService _environment;
    private readonly ILogger<PrerequisiteInstaller> _logger;

    public PrerequisiteInstaller(
        IOptions<PhoenixOptions> options,
        PhoenixPaths paths,
        IDownloadManager downloads,
        IPackageVerifier verifier,
        IEnvironmentService environment,
        ILogger<PrerequisiteInstaller> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _paths = paths;
        _downloads = downloads;
        _verifier = verifier;
        _environment = environment;
        _logger = logger;
    }

    public async Task<Result<PrerequisiteInstallOutcome>> InstallAsync(
        PrerequisiteDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var installer = definition.Installer;
        if (installer is null)
        {
            return PhoenixError.Prerequisite(
                "PX-PREREQ-NO-INSTALLER",
                $"'{definition.Id}' is missing but no installer is configured for it.");
        }

        if (_options.Testing.SimulatePrerequisiteInstalls)
        {
            _logger.LogWarning(
                "Simulating the installation of {Id}; nothing is being executed.",
                definition.Id);

            return new PrerequisiteInstallOutcome(
                definition.RequiresRestart,
                0,
                "Simulated installation (Testing:SimulatePrerequisiteInstalls).");
        }

        var acquired = await AcquireInstallerAsync(definition, installer, cancellationToken).ConfigureAwait(false);
        if (acquired.IsFailure)
        {
            return Result<PrerequisiteInstallOutcome>.Failure(acquired.Error);
        }

        var installerPath = acquired.Value;

        var signature = _verifier.VerifySignature(installerPath);
        if (signature.IsFailure)
        {
            return Result<PrerequisiteInstallOutcome>.Failure(signature.Error);
        }

        var arguments = CommandVersionDetector.SplitArguments(installer.SilentArguments);
        var timeout = TimeSpan.FromSeconds(Math.Max(30, definition.TimeoutSeconds));
        var needsElevation = definition.RequiresAdministrator && !_environment.IsElevated;

        _logger.LogInformation(
            "Running the installer for {Id} ({Installer}), elevated={Elevated}.",
            definition.Id,
            Path.GetFileName(installerPath),
            needsElevation);

        var result = needsElevation
            ? await ProcessRunner.RunElevatedAsync(installerPath, arguments, timeout, cancellationToken)
                .ConfigureAwait(false)
            : await ProcessRunner.RunAsync(installerPath, arguments, timeout, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        if (result.TimedOut)
        {
            return PhoenixError.Prerequisite(
                "PX-PREREQ-TIMEOUT",
                $"The installer for '{definition.Id}' did not finish within {timeout.TotalMinutes:0.#} minutes.");
        }

        // ERROR_CANCELLED: the user dismissed the elevation prompt. That is a choice, not a crash.
        if (needsElevation && result.ExitCode == 1223)
        {
            return PhoenixError.Permission(
                "PX-PREREQ-ELEVATION-DECLINED",
                $"Administrator permission is required to install '{definition.DisplayName}', and the " +
                "prompt was declined.");
        }

        if (installer.RebootExitCodes.Contains(result.ExitCode))
        {
            _logger.LogWarning(
                "Installer for {Id} finished with {ExitCode}: a restart is required.",
                definition.Id,
                result.ExitCode);

            return new PrerequisiteInstallOutcome(
                true,
                result.ExitCode,
                "Installed; the computer must restart before it can be used.");
        }

        if (!installer.AcceptedExitCodes.Contains(result.ExitCode))
        {
            return PhoenixError.Prerequisite(
                "PX-PREREQ-EXITCODE",
                $"The installer for '{definition.Id}' exited with {result.ExitCode}. " +
                $"Accepted codes: {string.Join(", ", installer.AcceptedExitCodes)}. " +
                $"Output: {Truncate(result.CombinedOutput)}");
        }

        return new PrerequisiteInstallOutcome(
            definition.RequiresRestart,
            result.ExitCode,
            $"Installer exited with {result.ExitCode}.");
    }

    private async Task<Result<string>> AcquireInstallerAsync(
        PrerequisiteDefinition definition,
        PrerequisiteInstallerOptions installer,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(installer.LocalPath))
        {
            var local = Environment.ExpandEnvironmentVariables(installer.LocalPath);
            if (!File.Exists(local))
            {
                return PhoenixError.Prerequisite(
                    "PX-PREREQ-LOCAL-MISSING",
                    $"The configured installer '{local}' for '{definition.Id}' does not exist.");
            }

            if (!string.IsNullOrWhiteSpace(installer.Sha256))
            {
                var verified = await _verifier
                    .VerifyChecksumAsync(local, installer.Sha256, cancellationToken)
                    .ConfigureAwait(false);

                if (verified.IsFailure)
                {
                    return Result<string>.Failure(verified.Error);
                }
            }

            return Result<string>.Success(local);
        }

        if (!Uri.TryCreate(installer.Url, UriKind.Absolute, out var url))
        {
            return PhoenixError.Configuration(
                "PX-PREREQ-URL",
                $"'{definition.Id}' has no usable installer URL.");
        }

        var fileName = string.IsNullOrWhiteSpace(installer.FileName)
            ? Path.GetFileName(url.LocalPath)
            : installer.FileName;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"{definition.Id}-installer";
        }

        var destination = Path.Combine(_paths.Cache, "prerequisites", definition.Id, fileName);

        // Reuse a previously verified download rather than fetching it again.
        if (File.Exists(destination) && !string.IsNullOrWhiteSpace(installer.Sha256))
        {
            var cached = await _verifier
                .VerifyChecksumAsync(destination, installer.Sha256, cancellationToken)
                .ConfigureAwait(false);

            if (cached.IsSuccess)
            {
                return Result<string>.Success(destination);
            }
        }

        var download = await _downloads
            .DownloadToFileAsync(
                DownloadDescriptor.For(url, installer.SizeBytes),
                destination,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (download.IsFailure)
        {
            return download;
        }

        var verification = await _verifier
            .VerifyChecksumAsync(destination, installer.Sha256 ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (verification.IsFailure)
        {
            TryDelete(destination);
            return Result<string>.Failure(verification.Error);
        }

        return Result<string>.Success(destination);
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value.Trim() : value[..500].Trim() + "...";

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}.", path);
        }
    }
}
