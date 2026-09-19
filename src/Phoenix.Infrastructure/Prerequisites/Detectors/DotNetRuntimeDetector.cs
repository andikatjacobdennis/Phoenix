using Microsoft.Extensions.Logging;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Models;
using Phoenix.Core.Versioning;
using Phoenix.Infrastructure.Processes;

namespace Phoenix.Infrastructure.Prerequisites.Detectors;

/// <summary>
/// Finds installed .NET shared frameworks (<c>Microsoft.NETCore.App</c>,
/// <c>Microsoft.AspNetCore.App</c>, <c>Microsoft.WindowsDesktop.App</c>).
///
/// The filesystem is the primary source because it works even when <c>dotnet</c> is not on the
/// PATH, which is common on machines where the runtime was installed but the SDK never was.
/// <c>dotnet --list-runtimes</c> is used as a second opinion.
/// </summary>
public sealed class DotNetRuntimeDetector : IPrerequisiteDetector
{
    private readonly ILogger<DotNetRuntimeDetector> _logger;

    public DotNetRuntimeDetector(ILogger<DotNetRuntimeDetector> logger) => _logger = logger;

    public DetectionStrategy Strategy => DetectionStrategy.DotNetRuntime;

    public async Task<DetectionResult> DetectAsync(
        PrerequisiteDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var runtimeName = definition.Detection.RuntimeName;
        if (string.IsNullOrWhiteSpace(runtimeName))
        {
            return DetectionResult.NotFound("No runtime name was configured.");
        }

        var versions = new List<SemanticVersion>();
        var sources = new List<string>();

        foreach (var root in EnumerateDotNetRoots())
        {
            var sharedDirectory = Path.Combine(root, "shared", runtimeName);
            if (!Directory.Exists(sharedDirectory))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(sharedDirectory))
            {
                if (SemanticVersion.TryParse(Path.GetFileName(directory), out var version))
                {
                    versions.Add(version);
                }
            }

            sources.Add(sharedDirectory);
        }

        if (versions.Count == 0)
        {
            var fromCli = await QueryDotNetCliAsync(runtimeName, cancellationToken).ConfigureAwait(false);
            versions.AddRange(fromCli);

            if (fromCli.Count > 0)
            {
                sources.Add("dotnet --list-runtimes");
            }
        }

        if (versions.Count == 0)
        {
            return DetectionResult.NotFound($"No {runtimeName} runtime was found.");
        }

        var ordered = versions.Distinct().OrderByDescending(v => v).ToList();

        return new DetectionResult
        {
            IsInstalled = true,
            DetectedVersion = ordered[0],
            AllDetectedVersions = ordered,
            Evidence = string.Join("; ", sources),
        };
    }

    private static IEnumerable<string> EnumerateDotNetRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in CandidateRoots())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var full = Path.GetFullPath(candidate);
            if (Directory.Exists(full) && seen.Add(full))
            {
                yield return full;
            }
        }
    }

    private static IEnumerable<string?> CandidateRoots()
    {
        yield return Environment.GetEnvironmentVariable("DOTNET_ROOT");
        yield return Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)");

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet");
        }
        else
        {
            yield return "/usr/share/dotnet";
            yield return "/usr/local/share/dotnet";
        }

        // Whatever directory the 'dotnet' on the PATH lives in.
        var onPath = ExecutableResolver.Resolve("dotnet");
        if (onPath is not null)
        {
            yield return Path.GetDirectoryName(onPath);
        }
    }

    private async Task<IReadOnlyList<SemanticVersion>> QueryDotNetCliAsync(
        string runtimeName,
        CancellationToken cancellationToken)
    {
        var dotnet = ExecutableResolver.Resolve("dotnet");
        if (dotnet is null)
        {
            return [];
        }

        var result = await ProcessRunner
            .RunAsync(dotnet, ["--list-runtimes"], TimeSpan.FromSeconds(30), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            _logger.LogDebug("'dotnet --list-runtimes' exited with {ExitCode}.", result.ExitCode);
            return [];
        }

        var versions = new List<SemanticVersion>();

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Lines look like: "Microsoft.AspNetCore.App 9.0.20 [C:\Program Files\dotnet\shared\...]"
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(runtimeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && SemanticVersion.TryParse(parts[1], out var version))
            {
                versions.Add(version);
            }
        }

        return versions;
    }
}
