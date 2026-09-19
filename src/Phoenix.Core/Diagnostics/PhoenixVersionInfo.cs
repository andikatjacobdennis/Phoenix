using System.Reflection;
using Phoenix.Core.Versioning;

namespace Phoenix.Core.Diagnostics;

/// <summary>
/// The version of Phoenix itself, taken from the assembly metadata that CI stamps.
/// Nothing hard-codes a version string.
/// </summary>
public static class PhoenixVersionInfo
{
    private static readonly Lazy<SemanticVersion> Lazy = new(Read);

    public static SemanticVersion Version => Lazy.Value;

    public static string DisplayVersion => Version.ToString();

    private static SemanticVersion Read()
    {
        var informational = typeof(PhoenixVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // The SDK appends "+<commit sha>" to the informational version.
        if (informational is not null)
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            if (plus > 0)
            {
                informational = informational[..plus];
            }

            if (SemanticVersion.TryParse(informational, out var parsed))
            {
                return parsed;
            }
        }

        var assemblyVersion = typeof(PhoenixVersionInfo).Assembly.GetName().Version;
        return assemblyVersion is null
            ? new SemanticVersion(0, 0, 0)
            : new SemanticVersion(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);
    }
}
