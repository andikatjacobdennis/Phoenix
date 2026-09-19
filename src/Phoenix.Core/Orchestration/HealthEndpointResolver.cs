using Phoenix.Core.Configuration;
using Phoenix.Core.Models;

namespace Phoenix.Core.Orchestration;

/// <summary>
/// Works out which URL to poll. Precedence: explicit health-check URL, then the manifest's
/// health endpoint applied to the application URL, then the configured health path.
/// </summary>
public static class HealthEndpointResolver
{
    public static Uri Resolve(PhoenixOptions options, InstallationInfo? installation)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.HealthCheck.Url) &&
            Uri.TryCreate(options.HealthCheck.Url, UriKind.Absolute, out var explicitUrl))
        {
            return explicitUrl;
        }

        var baseUrl = new Uri(options.Application.Url, UriKind.Absolute);

        var path = installation?.HealthEndpoint;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = options.HealthCheck.Path;
        }

        return new Uri(baseUrl, path);
    }

    public static Uri ResolveApplicationUrl(PhoenixOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configured = string.IsNullOrWhiteSpace(options.Browser.Url)
            ? options.Application.Url
            : options.Browser.Url;

        return new Uri(configured, UriKind.Absolute);
    }
}
