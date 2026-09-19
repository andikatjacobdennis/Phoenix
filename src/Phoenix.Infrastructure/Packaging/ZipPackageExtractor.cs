using System.Globalization;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Errors;
using Phoenix.Infrastructure.Testing;

namespace Phoenix.Infrastructure.Packaging;

/// <summary>
/// Extracts a zip archive into a staging directory.
///
/// Archives arrive from the network, so every entry is treated as hostile:
/// <list type="bullet">
///   <item>rooted paths, drive letters and <c>..</c> segments are refused outright;</item>
///   <item>the resolved destination of every entry must stay inside the target directory;</item>
///   <item>entry count and uncompressed size are capped, which bounds zip-bomb damage.</item>
/// </list>
/// A rejected entry aborts the whole extraction: a partially extracted package is never used.
/// </summary>
public sealed class ZipPackageExtractor : IPackageExtractor
{
    private readonly PhoenixOptions _options;
    private readonly FaultInjector _faults;
    private readonly ILogger<ZipPackageExtractor> _logger;

    public ZipPackageExtractor(
        IOptions<PhoenixOptions> options,
        FaultInjector faults,
        ILogger<ZipPackageExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _faults = faults;
        _logger = logger;
    }

    public async Task<Result<ExtractionSummary>> ExtractAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
        {
            return PhoenixError.Installation("PX-EXTRACT-MISSING", $"Archive '{archivePath}' does not exist.");
        }

        if (_faults.ShouldCorruptArchive)
        {
            _logger.LogWarning("Injecting a corrupt-archive failure for {Archive}.", Path.GetFileName(archivePath));
            return PhoenixError.Verification(
                "PX-EXTRACT-CORRUPT",
                "Simulated corrupt archive (Phoenix fault injection).");
        }

        Directory.CreateDirectory(destinationDirectory);

        var root = Path.GetFullPath(destinationDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            if (archive.Entries.Count > _options.Security.MaxArchiveEntries)
            {
                return PhoenixError.Security(
                    "PX-EXTRACT-TOO-MANY",
                    $"Archive contains {archive.Entries.Count} entries, over the configured limit of " +
                    $"{_options.Security.MaxArchiveEntries}.");
            }

            long declaredBytes = 0;
            foreach (var entry in archive.Entries)
            {
                declaredBytes += entry.Length;
                if (declaredBytes > _options.Security.MaxExtractedSizeBytes)
                {
                    return PhoenixError.Security(
                        "PX-EXTRACT-TOO-BIG",
                        $"Archive expands to more than the configured limit of " +
                        $"{_options.Security.MaxExtractedSizeBytes} bytes.");
                }
            }

            long written = 0;
            var files = 0;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var validation = ValidateEntryName(entry.FullName);
                if (validation.IsFailure)
                {
                    return Result<ExtractionSummary>.Failure(validation.Error);
                }

                var targetPath = Path.GetFullPath(Path.Combine(root, entry.FullName));

                // The decisive check: after normalisation the entry must still be inside the root.
                if (!targetPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(targetPath, root, StringComparison.OrdinalIgnoreCase))
                {
                    return PhoenixError.Security(
                        "PX-EXTRACT-TRAVERSAL",
                        $"Archive entry '{entry.FullName}' resolves outside the extraction directory.");
                }

                // A directory entry has an empty name and a path ending in a separator.
                if (entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                await using var source = entry.Open();
                await using var target = new FileStream(
                    targetPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);

                written += await CopyBoundedAsync(source, target, cancellationToken).ConfigureAwait(false);
                files++;

                if (written > _options.Security.MaxExtractedSizeBytes)
                {
                    return PhoenixError.Security(
                        "PX-EXTRACT-TOO-BIG",
                        "Archive exceeded the configured extracted-size limit while extracting.");
                }
            }

            _logger.LogInformation(
                "Extracted {Files} files ({Bytes} bytes) from {Archive}.",
                files.ToString(CultureInfo.InvariantCulture),
                written.ToString(CultureInfo.InvariantCulture),
                Path.GetFileName(archivePath));

            return new ExtractionSummary(files, written);
        }
        catch (InvalidDataException ex)
        {
            return PhoenixError.Verification(
                "PX-EXTRACT-CORRUPT",
                $"'{Path.GetFileName(archivePath)}' is not a readable zip archive: {ex.Message}",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return PhoenixError.Permission(
                "PX-EXTRACT-DENIED",
                $"Extracting into '{destinationDirectory}' was denied: {ex.Message}",
                ex);
        }
        catch (IOException ex)
        {
            return PhoenixError.Installation(
                "PX-EXTRACT-IO",
                $"Extracting '{Path.GetFileName(archivePath)}' failed: {ex.Message}",
                ex);
        }
    }

    private static async Task<long> CopyBoundedAsync(Stream source, Stream target, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }
    }

    /// <summary>
    /// Rejects the shapes that lead outside the destination before any path maths happens.
    /// Kept internal so the tests can exercise it directly.
    /// </summary>
    internal static Result ValidateEntryName(string? entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return PhoenixError.Security("PX-EXTRACT-EMPTY-ENTRY", "Archive contains an entry with an empty name.");
        }

        // Checked before the rooted test so "C:\..." is reported as what it is.
        if (entryName.Contains(':', StringComparison.Ordinal))
        {
            return PhoenixError.Security(
                "PX-EXTRACT-DRIVE",
                $"Archive entry '{entryName}' contains a drive or stream qualifier.");
        }

        if (Path.IsPathRooted(entryName) || entryName.StartsWith('/') || entryName.StartsWith('\\'))
        {
            return PhoenixError.Security(
                "PX-EXTRACT-ABSOLUTE",
                $"Archive entry '{entryName}' uses an absolute path.");
        }

        foreach (var segment in entryName.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return PhoenixError.Security(
                    "PX-EXTRACT-TRAVERSAL",
                    $"Archive entry '{entryName}' contains a parent-directory segment.");
            }
        }

        return Result.Success();
    }
}
