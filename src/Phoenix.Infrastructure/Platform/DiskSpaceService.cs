using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Errors;

namespace Phoenix.Infrastructure.Platform;

/// <summary>
/// Refuses to start work that predictably cannot finish. Running out of space halfway through
/// an extraction is one of the easier ways to end up with a broken installation.
/// </summary>
public sealed class DiskSpaceService : IDiskSpaceService
{
    private readonly PhoenixOptions _options;
    private readonly ILogger<DiskSpaceService> _logger;

    public DiskSpaceService(IOptions<PhoenixOptions> options, ILogger<DiskSpaceService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _logger = logger;
    }

    public long GetAvailableFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return long.MaxValue;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not determine free space for {Path}.", path);

            // If the free space cannot be read, do not block the run on a guess.
            return long.MaxValue;
        }
    }

    public Result EnsureAvailable(string path, long requiredBytes)
    {
        var available = GetAvailableFreeBytes(path);

        if (available >= requiredBytes)
        {
            return Result.Success();
        }

        var technical =
            $"'{path}' has {FormatBytes(available)} free but {FormatBytes(requiredBytes)} is required " +
            $"(including a {FormatBytes(_options.Security.DiskSpaceMarginBytes)} safety margin).";

        _logger.LogError("Insufficient disk space: {Detail}", technical);

        return PhoenixError.DiskSpace("PX-DISK-SPACE", technical);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
