using System.Reflection;

namespace Phoenix.DemoApp;

/// <summary>
/// What the demo application knows about itself. The version comes from the build, never from
/// a constant, so a new GitHub release visibly changes the page after Phoenix updates it.
/// </summary>
public sealed record DemoInfo(
    string Company,
    string Product,
    string Version,
    string Environment,
    string MachineName,
    DateTimeOffset StartedAt,
    string InstallationPath)
{
    /// <summary>
    /// Set to <c>crash</c> or <c>unhealthy</c> by Phoenix fault injection so the rollback path
    /// can be demonstrated against a real failing application. Unset in normal use.
    /// </summary>
    public const string SimulationVariable = "PHOENIX_DEMO_SIMULATE";

    public static string ReadVersion()
    {
        var informational = typeof(DemoInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(DemoInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // Strip the "+<commit sha>" suffix the SDK appends.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }

    public static string? Simulation =>
        System.Environment.GetEnvironmentVariable(SimulationVariable)?.Trim().ToLowerInvariant();
}
