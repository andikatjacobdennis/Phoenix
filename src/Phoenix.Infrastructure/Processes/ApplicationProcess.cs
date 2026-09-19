using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Phoenix.Core.Abstractions;

namespace Phoenix.Infrastructure.Processes;

/// <summary>
/// A handle on the running application.
///
/// Disposing this object releases the handle and nothing else: the application is meant to
/// outlive Phoenix. Stopping it is always an explicit decision, made during rollback or when
/// an update needs the files back.
/// </summary>
internal sealed class ApplicationProcess : IApplicationProcess
{
    private readonly Process _process;
    private readonly TimeSpan _shutdownTimeout;
    private readonly ILogger _logger;
    private bool _disposed;

    public ApplicationProcess(Process process, TimeSpan shutdownTimeout, ILogger logger)
    {
        _process = process;
        _shutdownTimeout = shutdownTimeout;
        _logger = logger;
        ProcessId = process.Id;
    }

    public int ProcessId { get; }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_process.HasExited)
            {
                return;
            }

            _logger.LogInformation("Stopping application process {ProcessId}.", ProcessId);

            // Ask first. A web application given the chance will shut down its listeners
            // and release the files Phoenix is about to replace.
            if (!_process.CloseMainWindow())
            {
                _process.Kill(entireProcessTree: true);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_shutdownTimeout);

            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!_process.HasExited)
                {
                    _logger.LogWarning(
                        "Process {ProcessId} did not stop within {Timeout}; terminating it.",
                        ProcessId,
                        _shutdownTimeout);

                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Process {ProcessId} was already gone.", ProcessId);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _process.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
