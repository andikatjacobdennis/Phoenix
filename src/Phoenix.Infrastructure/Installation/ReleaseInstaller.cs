using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Diagnostics;
using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Core.Orchestration;
using Phoenix.Core.Releases;

namespace Phoenix.Infrastructure.Installation;

/// <summary>
/// Turns a release into a validated directory under <c>versions/</c>, following a strict order:
///
/// <code>
/// manifest -> validate -> disk space -> download -> checksum -> signature
///          -> extract to staging -> validate payload -> move into versions/
/// </code>
///
/// Nothing that is currently installed is touched. If any step fails, the staging directory is
/// discarded and the machine is exactly where it started.
/// </summary>
public sealed class ReleaseInstaller : IReleaseInstaller
{
    private const long MaxManifestBytes = 256 * 1024;

    private readonly PhoenixOptions _options;
    private readonly PhoenixPaths _paths;
    private readonly PhoenixRunOptions _run;
    private readonly IReleaseProvider _provider;
    private readonly IDownloadManager _downloads;
    private readonly IPackageVerifier _verifier;
    private readonly IPackageExtractor _extractor;
    private readonly IDiskSpaceService _diskSpace;
    private readonly IEnvironmentService _environment;
    private readonly ILogger<ReleaseInstaller> _logger;

    public ReleaseInstaller(
        IOptions<PhoenixOptions> options,
        PhoenixPaths paths,
        PhoenixRunOptions run,
        IReleaseProvider provider,
        IDownloadManager downloads,
        IPackageVerifier verifier,
        IPackageExtractor extractor,
        IDiskSpaceService diskSpace,
        IEnvironmentService environment,
        ILogger<ReleaseInstaller> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _paths = paths;
        _run = run;
        _provider = provider;
        _downloads = downloads;
        _verifier = verifier;
        _extractor = extractor;
        _diskSpace = diskSpace;
        _environment = environment;
        _logger = logger;
    }

    public async Task<Result<ResolvedRelease>> ResolveAsync(
        ReleaseCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var descriptor = _provider.DescribeAsset(candidate.ManifestAsset);
        var json = await _downloads.DownloadTextAsync(descriptor, MaxManifestBytes, cancellationToken)
            .ConfigureAwait(false);

        if (json.IsFailure)
        {
            return Result<ResolvedRelease>.Failure(json.Error);
        }

        var parsed = ReleaseManifestSerializer.Deserialize(json.Value);
        if (parsed.IsFailure)
        {
            return Result<ResolvedRelease>.Failure(parsed.Error);
        }

        var system = _environment.GetSystemInformation();

        var validation = ManifestValidator.Validate(
            parsed.Value,
            new ManifestValidationContext
            {
                ExpectedApplicationId = _options.Application.Id,
                ExpectedChannel = _options.Updates.Channel,
                ReleaseVersion = candidate.Version,
                OperatingSystem = system.OperatingSystemMoniker,
                Architecture = system.Architecture,
                PhoenixVersion = PhoenixVersionInfo.Version,
                RequireChecksum = _options.Security.RequireChecksum,
            });

        if (validation.IsFailure)
        {
            return Result<ResolvedRelease>.Failure(validation.Error);
        }

        var package = validation.Value;
        var asset = candidate.Release.FindAsset(package.PackageFile);

        if (asset is null)
        {
            return PhoenixError.Verification(
                "PX-RELEASE-ASSET-MISSING",
                $"Release {candidate.Release.Tag} does not contain the asset '{package.PackageFile}' " +
                "named by its manifest.");
        }

        _logger.LogInformation(
            "Resolved {Tag}: package {Package} ({Bytes} bytes), executable {Executable}.",
            candidate.Release.Tag,
            package.PackageFile,
            package.SizeBytes,
            package.Executable);

        return new ResolvedRelease
        {
            Candidate = candidate,
            Manifest = parsed.Value,
            Package = package,
            PackageAsset = asset,
        };
    }

    public async Task<Result<InstallationInfo>> InstallAsync(
        ResolvedRelease resolved,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var version = resolved.Version.ToString();
        var package = resolved.Package;

        var declaredSize = package.SizeBytes > 0 ? package.SizeBytes : resolved.PackageAsset.SizeBytes;
        var space = _diskSpace.EnsureAvailable(_paths.Root, EstimateRequiredBytes(declaredSize));
        if (space.IsFailure)
        {
            return Result<InstallationInfo>.Failure(space.Error);
        }

        var stagingDirectory = _paths.StagingDirectory($"{_run.OperationId}-{version}");
        var payloadDirectory = Path.Combine(stagingDirectory, "payload");
        var packagePath = Path.Combine(_paths.Cache, version, package.PackageFile);

        try
        {
            Directory.CreateDirectory(stagingDirectory);

            var download = await AcquirePackageAsync(resolved, packagePath, progress, cancellationToken)
                .ConfigureAwait(false);

            if (download.IsFailure)
            {
                return Result<InstallationInfo>.Failure(download.Error);
            }

            var extraction = await _extractor
                .ExtractAsync(packagePath, payloadDirectory, cancellationToken)
                .ConfigureAwait(false);

            if (extraction.IsFailure)
            {
                // A package that will not extract is not worth keeping in the cache.
                TryDeleteFile(packagePath);
                return Result<InstallationInfo>.Failure(extraction.Error);
            }

            var payloadRoot = ResolvePayloadRoot(payloadDirectory, package.Executable);
            var executablePath = Path.Combine(payloadRoot, package.Executable);

            if (!File.Exists(executablePath))
            {
                return PhoenixError.Installation(
                    "PX-INSTALL-NO-EXE",
                    $"The package for {version} does not contain '{package.Executable}'.",
                    requiresRollback: false);
            }

            var signature = _verifier.VerifySignature(executablePath);
            if (signature.IsFailure)
            {
                return Result<InstallationInfo>.Failure(signature.Error);
            }

            await InstalledPackageStore.WriteAsync(
                payloadRoot,
                new InstalledPackageDescriptor
                {
                    ApplicationId = resolved.Manifest.ApplicationId,
                    Version = version,
                    Channel = resolved.Manifest.Channel,
                    Executable = package.Executable,
                    Arguments = package.Arguments,
                    HealthEndpoint = package.HealthEndpoint,
                    EnvironmentVariables = package.EnvironmentVariables,
                    SourceTag = resolved.Candidate.Release.Tag,
                    PackageSha256 = package.Sha256,
                },
                cancellationToken).ConfigureAwait(false);

            var versionDirectory = _paths.VersionDirectory(version);

            // The candidate is complete in staging; moving it in is the last filesystem step.
            if (Directory.Exists(versionDirectory) && !DirectoryOperations.TryDelete(versionDirectory))
            {
                return PhoenixError.Installation(
                    "PX-INSTALL-LOCKED",
                    $"'{versionDirectory}' already exists and could not be replaced. A file may be in use.",
                    requiresRollback: false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(versionDirectory)!);
            DirectoryOperations.Move(payloadRoot, versionDirectory);

            var installation = InstalledPackageStore.TryReadInstallation(versionDirectory, isKnownGood: false);
            if (installation is null)
            {
                return PhoenixError.Installation(
                    "PX-INSTALL-INVALID",
                    $"The installed directory for {version} did not validate after being moved into place.");
            }

            _logger.LogInformation(
                "Prepared version {Version} at {Path} ({Files} files).",
                version,
                versionDirectory,
                extraction.Value.EntryCount);

            return installation;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PhoenixError.Installation(
                "PX-INSTALL-IO",
                $"Installing {version} failed: {ex.Message}",
                ex);
        }
        finally
        {
            DirectoryOperations.TryDelete(stagingDirectory);
        }
    }

    private async Task<Result> AcquirePackageAsync(
        ResolvedRelease resolved,
        string packagePath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var package = resolved.Package;

        // A cached package is only reused when it still matches the expected checksum.
        if (File.Exists(packagePath) && _options.Security.RequireChecksum)
        {
            var cached = await _verifier
                .VerifyChecksumAsync(packagePath, package.Sha256, cancellationToken)
                .ConfigureAwait(false);

            if (cached.IsSuccess)
            {
                _logger.LogInformation("Reusing the verified cached package for {Version}.", resolved.Version);
                progress?.Report(new DownloadProgress(package.SizeBytes, package.SizeBytes));
                return Result.Success();
            }

            _logger.LogWarning("Cached package for {Version} failed verification; downloading again.", resolved.Version);
            TryDeleteFile(packagePath);
        }

        var descriptor = _provider.DescribeAsset(resolved.PackageAsset);
        var download = await _downloads
            .DownloadToFileAsync(descriptor, packagePath, progress, cancellationToken)
            .ConfigureAwait(false);

        if (download.IsFailure)
        {
            return Result.Failure(download.Error);
        }

        var verification = await _verifier
            .VerifyChecksumAsync(packagePath, package.Sha256, cancellationToken)
            .ConfigureAwait(false);

        if (verification.IsFailure)
        {
            TryDeleteFile(packagePath);
            return verification;
        }

        return Result.Success();
    }

    /// <summary>
    /// Packages are usually zipped with the application at the root, but a single wrapping
    /// folder is common enough to handle. Anything else is rejected by the executable check.
    /// </summary>
    private static string ResolvePayloadRoot(string payloadDirectory, string relativeExecutable)
    {
        if (File.Exists(Path.Combine(payloadDirectory, relativeExecutable)))
        {
            return payloadDirectory;
        }

        var directories = Directory.GetDirectories(payloadDirectory);
        if (directories.Length == 1 &&
            Directory.GetFiles(payloadDirectory).Length == 0 &&
            File.Exists(Path.Combine(directories[0], relativeExecutable)))
        {
            return directories[0];
        }

        return payloadDirectory;
    }

    /// <summary>
    /// Room for the download, the extracted copy, and the directory move, plus the configured
    /// margin. Deliberately pessimistic: starting an install that cannot finish is worse than
    /// refusing it.
    /// </summary>
    private long EstimateRequiredBytes(long packageSize)
    {
        var compressed = Math.Max(packageSize, 1024 * 1024);
        return (compressed * 4) + _options.Security.DiskSpaceMarginBytes;
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}.", path);
        }
    }
}
