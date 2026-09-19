using System.Security;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Diagnostics;
using Phoenix.Core.Models;

namespace Phoenix.Infrastructure.Platform;

/// <summary>Everything Phoenix knows about the machine and the person running it.</summary>
public sealed class EnvironmentService : IEnvironmentService
{
    private readonly ILogger<EnvironmentService> _logger;
    private SystemInformation? _cached;

    public EnvironmentService(string environmentName, ILogger<EnvironmentService> logger)
    {
        EnvironmentName = environmentName;
        _logger = logger;
    }

    public string EnvironmentName { get; }

    public bool IsElevated => DetectElevation();

    public SystemInformation GetSystemInformation() => _cached ??= Build();

    private SystemInformation Build() => new()
    {
        UserDisplayName = ResolveUserDisplayName(),
        MachineName = Environment.MachineName,
        OperatingSystem = RuntimeInformation.OSDescription,
        OperatingSystemMoniker = ResolveOperatingSystemMoniker(),
        Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        IsElevated = DetectElevation(),
        IsInteractive = Environment.UserInteractive && !Console.IsOutputRedirected,
        PhoenixVersion = PhoenixVersionInfo.DisplayVersion,
        DotNetRuntimeVersion = RuntimeInformation.FrameworkDescription,
    };

    private static string ResolveOperatingSystemMoniker()
    {
        if (OperatingSystem.IsWindows())
        {
            return "win";
        }

        return OperatingSystem.IsMacOS() ? "osx" : "linux";
    }

    /// <summary>
    /// A first name if Windows knows one ("Welcome, Sarah."), otherwise a tidied account name.
    /// Never fails: a greeting is not worth an exception.
    /// </summary>
    private string ResolveUserDisplayName()
    {
        var display = TryGetWindowsDisplayName();

        if (string.IsNullOrWhiteSpace(display))
        {
            display = Environment.UserName;
        }

        return Prettify(display);
    }

    internal static string Prettify(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return "there";
        }

        var name = accountName.Trim();

        // "CONTOSO\sarah.jones" -> "sarah.jones"
        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0 && slash < name.Length - 1)
        {
            name = name[(slash + 1)..];
        }

        // "sarah.jones@contoso.com" -> "sarah.jones"
        var at = name.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            name = name[..at];
        }

        // "Jones, Sarah" -> "Sarah"
        var comma = name.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0 && comma < name.Length - 1)
        {
            name = name[(comma + 1)..].Trim();
        }

        // "sarah.jones" or "sarah jones" -> "sarah"
        var separator = name.IndexOfAny(['.', '_', ' ']);
        if (separator > 0)
        {
            name = name[..separator];
        }

        if (name.Length == 0)
        {
            return "there";
        }

        return char.ToUpper(name[0], CultureInfo.InvariantCulture) + name[1..];
    }

    private string? TryGetWindowsDisplayName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var size = 256;
            var builder = new StringBuilder(size);

            // NameDisplay (3) is the friendly name, when the machine is domain-joined or
            // the local account has one configured.
            if (NativeMethods.GetUserNameEx(3, builder, ref size))
            {
                return builder.ToString();
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            _logger.LogDebug(ex, "GetUserNameEx is unavailable on this system.");
        }

        return null;
    }

    private bool DetectElevation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            _logger.LogDebug(ex, "Could not determine whether the process is elevated.");
            return false;
        }
    }

    public bool TryRelaunchElevated(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            _logger.LogInformation("Relaunched Phoenix elevated as process {ProcessId}.", process.Id);
            return true;
        }
        catch (Win32Exception ex)
        {
            // 1223 is ERROR_CANCELLED: the user declined the elevation prompt.
            _logger.LogWarning("Elevation was not granted ({Message}).", ex.Message);
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool GetUserNameEx(int nameFormat, StringBuilder name, ref int size);
    }
}
