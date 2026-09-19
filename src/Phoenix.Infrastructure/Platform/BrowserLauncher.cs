using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Errors;

namespace Phoenix.Infrastructure.Platform;

/// <summary>
/// Opens the application in the user's default browser, once, after the health check has
/// passed. Phoenix never opens a tab per retry.
/// </summary>
public sealed class BrowserLauncher : IBrowserLauncher
{
    private readonly ILogger<BrowserLauncher> _logger;

    public BrowserLauncher(ILogger<BrowserLauncher> logger) => _logger = logger;

    public Result Open(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            return PhoenixError.Security(
                "PX-BROWSER-SCHEME",
                $"Refusing to open '{url.Scheme}' in a browser; only http and https are allowed.");
        }

        try
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(url.ToString()) { UseShellExecute = true }
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", url.ToString())
                    : new ProcessStartInfo("xdg-open", url.ToString());

            using var process = Process.Start(startInfo);
            _logger.LogInformation("Opened {Url} in the default browser.", url);
            return Result.Success();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return PhoenixError.Unexpected(
                "PX-BROWSER-FAILED",
                $"Could not open {url} in a browser: {ex.Message}",
                ex);
        }
    }
}
