using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Infrastructure.Testing;

namespace Phoenix.Infrastructure.Processes;

/// <summary>
/// Starts the application and watches it long enough to notice an immediate crash.
///
/// Creating a process successfully proves almost nothing: an application can exit one second
/// later because a port is taken or a config file is missing. The startup grace period turns
/// that into a real error instead of a silent failure discovered by the health check timeout.
/// </summary>
public sealed class ApplicationLauncher : IApplicationLauncher
{
    private readonly PhoenixOptions _options;
    private readonly PhoenixPaths _paths;
    private readonly FaultInjector _faults;
    private readonly ILogger<ApplicationLauncher> _logger;

    public ApplicationLauncher(
        IOptions<PhoenixOptions> options,
        PhoenixPaths paths,
        FaultInjector faults,
        ILogger<ApplicationLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _paths = paths;
        _faults = faults;
        _logger = logger;
    }

    public async Task<Result<IApplicationProcess>> StartAsync(
        InstallationInfo installation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);

        if (!File.Exists(installation.ExecutablePath))
        {
            return PhoenixError.Startup(
                "PX-START-NO-EXE",
                $"'{installation.ExecutablePath}' does not exist.");
        }

        var startInfo = BuildStartInfo(installation);

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                return PhoenixError.Startup(
                    "PX-START-NO-PROCESS",
                    $"Starting '{installation.ExecutablePath}' did not produce a process.");
            }

            AttachOutputLogging(process, installation);

            _logger.LogInformation(
                "Started {Executable} as process {ProcessId} with arguments [{Arguments}].",
                installation.RelativeExecutable,
                process.Id,
                string.Join(' ', startInfo.ArgumentList));

            // Give it a moment to fall over, which is the most common failure mode.
            var grace = TimeSpan.FromSeconds(Math.Max(0, _options.Application.StartupGraceSeconds));
            if (grace > TimeSpan.Zero)
            {
                using var graceTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                graceTimeout.CancelAfter(grace);

                try
                {
                    await process.WaitForExitAsync(graceTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Still running after the grace period, which is what we want.
                }
            }

            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                process.Dispose();

                return PhoenixError.Startup(
                    "PX-START-IMMEDIATE-EXIT",
                    $"'{installation.RelativeExecutable}' exited with code {exitCode} within " +
                    $"{grace.TotalSeconds:0.#}s of starting.");
            }

            return Result<IApplicationProcess>.Success(
                new ApplicationProcess(
                    process,
                    TimeSpan.FromSeconds(_options.Application.ShutdownTimeoutSeconds),
                    _logger));
        }
        catch (Win32Exception ex)
        {
            return PhoenixError.Startup(
                "PX-START-WIN32",
                $"Starting '{installation.ExecutablePath}' failed: {ex.Message} (native code {ex.NativeErrorCode}).",
                ex);
        }
        catch (InvalidOperationException ex)
        {
            return PhoenixError.Startup(
                "PX-START-INVALID",
                $"Starting '{installation.ExecutablePath}' failed: {ex.Message}",
                ex);
        }
    }

    private ProcessStartInfo BuildStartInfo(InstallationInfo installation)
    {
        var startInfo = new ProcessStartInfo(installation.ExecutablePath)
        {
            WorkingDirectory = installation.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in installation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in _options.Application.AdditionalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in installation.EnvironmentVariables)
        {
            startInfo.Environment[key] = value;
        }

        foreach (var (key, value) in _options.Application.EnvironmentVariables)
        {
            startInfo.Environment[key] = value;
        }

        // Fault injection makes the application itself misbehave, so the failure Phoenix
        // recovers from is a real one rather than a simulated verdict.
        var simulation = _faults.ChildProcessSimulation;
        if (simulation is not null)
        {
            startInfo.Environment[FaultInjector.ChildSimulationVariable] = simulation;
            _logger.LogWarning("Starting the application with {Variable}={Value}.",
                FaultInjector.ChildSimulationVariable, simulation);
        }

        return startInfo;
    }

    /// <summary>
    /// Drains the child's output into a per-version log file. Without draining, a chatty
    /// application would eventually block on a full pipe.
    /// </summary>
    private void AttachOutputLogging(Process process, InstallationInfo installation)
    {
        try
        {
            Directory.CreateDirectory(_paths.Logs);
            var logPath = Path.Combine(_paths.Logs, $"application-{installation.Version}.log");

            var writer = new StreamWriter(
                new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };

            var open = 2;

            void Handle(object? sender, DataReceivedEventArgs args)
            {
                if (args.Data is null)
                {
                    if (Interlocked.Decrement(ref open) == 0)
                    {
                        writer.Dispose();
                    }

                    return;
                }

                try
                {
                    writer.WriteLine(args.Data);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // The application outlives Phoenix; losing the tail of its log is fine.
                }
            }

            process.OutputDataReceived += Handle;
            process.ErrorDataReceived += Handle;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Could not attach application output logging.");
        }
    }

    public IApplicationProcess? FindRunningInstance(InstallationInfo installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        foreach (var process in EnumerateProcessesUnder(installation.Path))
        {
            return new ApplicationProcess(
                process,
                TimeSpan.FromSeconds(_options.Application.ShutdownTimeoutSeconds),
                _logger);
        }

        return null;
    }

    public async Task StopRunningInstancesAsync(string installationPath, CancellationToken cancellationToken)
    {
        foreach (var process in EnumerateProcessesUnder(installationPath))
        {
            await using var handle = new ApplicationProcess(
                process,
                TimeSpan.FromSeconds(_options.Application.ShutdownTimeoutSeconds),
                _logger);

            await handle.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds processes whose executable lives under the given directory. Reading another
    /// process's module path can be refused, so failures are ignored rather than fatal.
    /// </summary>
    private IEnumerable<Process> EnumerateProcessesUnder(string directory)
    {
        var root = Path.GetFullPath(directory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        foreach (var process in Process.GetProcesses())
        {
            string? path = null;

            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // System and cross-architecture processes cannot be inspected; skip them.
            }

            if (path is not null &&
                path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                yield return process;
                continue;
            }

            process.Dispose();
        }
    }
}
