using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Versioning;

namespace Phoenix.Infrastructure.Installation;

/// <summary>
/// Housekeeping: abandoned staging folders, cached packages, superseded versions and old
/// backups. Two rules are absolute - the active installation is never touched, and the
/// known-good copy is never the thing that gets deleted.
/// </summary>
public sealed class CleanupService : ICleanupService
{
    private static readonly TimeSpan StagingMaxAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(14);

    private readonly PhoenixPaths _paths;
    private readonly PhoenixOptions _options;
    private readonly IStateStore _state;
    private readonly ILogger<CleanupService> _logger;

    public CleanupService(
        PhoenixPaths paths,
        IOptions<PhoenixOptions> options,
        IStateStore state,
        ILogger<CleanupService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _paths = paths;
        _options = options.Value;
        _state = state;
        _logger = logger;
    }

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var state = await _state.LoadAsync(cancellationToken).ConfigureAwait(false);

        var protectedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state.InstalledVersion is not null)
        {
            protectedVersions.Add(state.InstalledVersion);
        }

        if (state.KnownGoodVersion is not null)
        {
            protectedVersions.Add(state.KnownGoodVersion);
        }

        CleanStaging(cancellationToken);
        CleanVersions(protectedVersions, cancellationToken);
        CleanCache(protectedVersions, cancellationToken);
    }

    private void CleanStaging(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.Staging))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_paths.Staging))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Phoenix holds a single-instance lock, so anything still here is abandoned.
            // The age check only protects against a clock or a stale lock surprise.
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < StagingMaxAge)
            {
                continue;
            }

            if (DirectoryOperations.TryDelete(directory))
            {
                _logger.LogInformation("Removed abandoned staging directory {Path}.", directory);
            }
        }
    }

    private void CleanVersions(HashSet<string> protectedVersions, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.Versions))
        {
            return;
        }

        var versions = Directory.EnumerateDirectories(_paths.Versions)
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .Where(v => SemanticVersion.TryParse(v.Name, out _))
            .OrderByDescending(v => SemanticVersion.Parse(v.Name))
            .ToList();

        var keep = Math.Max(1, _options.Recovery.KeepVersions);
        var index = 0;

        foreach (var version in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (protectedVersions.Contains(version.Name))
            {
                continue;
            }

            index++;
            if (index <= keep)
            {
                continue;
            }

            if (DirectoryOperations.TryDelete(version.Path))
            {
                _logger.LogInformation("Removed superseded version {Version}.", version.Name);
            }
        }
    }

    private void CleanCache(HashSet<string> protectedVersions, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.Cache))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_paths.Cache))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(directory);
            if (protectedVersions.Contains(name))
            {
                continue;
            }

            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < CacheMaxAge)
            {
                continue;
            }

            if (DirectoryOperations.TryDelete(directory))
            {
                _logger.LogInformation("Removed expired cache entry {Version}.", name);
            }
        }

        // Partial downloads left by an interrupted run.
        foreach (var partial in Directory.EnumerateFiles(_paths.Cache, "*.part", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(partial);
                _logger.LogInformation("Removed partial download {File}.", Path.GetFileName(partial));
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not delete partial download {File}.", partial);
            }
        }
    }
}
