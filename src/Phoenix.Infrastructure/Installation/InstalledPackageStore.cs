using System.Text.Json;
using System.Text.Json.Serialization;
using Phoenix.Core.Models;
using Phoenix.Core.Versioning;

namespace Phoenix.Infrastructure.Installation;

/// <summary>Reads and writes the per-version <c>.phoenix-install.json</c> descriptor.</summary>
internal static class InstalledPackageStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task WriteAsync(
        string versionDirectory,
        InstalledPackageDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(versionDirectory, InstalledPackageDescriptor.FileName);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, descriptor, Options, cancellationToken).ConfigureAwait(false);
    }

    public static InstalledPackageDescriptor? TryRead(string versionDirectory)
    {
        var path = Path.Combine(versionDirectory, InstalledPackageDescriptor.FileName);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<InstalledPackageDescriptor>(stream, Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds an <see cref="InstallationInfo"/> from a version directory, or null when the
    /// directory is not a usable installation (no descriptor, or the executable is missing).
    /// </summary>
    public static InstallationInfo? TryReadInstallation(string versionDirectory, bool isKnownGood)
    {
        var descriptor = TryRead(versionDirectory);
        if (descriptor is null)
        {
            return null;
        }

        if (!SemanticVersion.TryParse(descriptor.Version, out var version))
        {
            return null;
        }

        var executable = Path.Combine(versionDirectory, descriptor.Executable);
        if (!File.Exists(executable))
        {
            return null;
        }

        return new InstallationInfo
        {
            Version = version,
            Path = versionDirectory,
            RelativeExecutable = descriptor.Executable,
            Arguments = descriptor.Arguments,
            HealthEndpoint = descriptor.HealthEndpoint,
            EnvironmentVariables = descriptor.EnvironmentVariables,
            IsKnownGood = isKnownGood,
        };
    }
}
