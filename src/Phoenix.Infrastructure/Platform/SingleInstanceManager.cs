using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Errors;

namespace Phoenix.Infrastructure.Platform;

/// <summary>
/// One Phoenix at a time per installation root. Two instances updating the same directory is
/// the fastest way to produce an installation that is neither the old version nor the new one.
///
/// The lock is a named mutex derived from the installation root, so two products on the same
/// machine, or the same product under two roots, do not block each other.
/// </summary>
public sealed class SingleInstanceManager : ISingleInstanceManager
{
    private readonly ILogger<SingleInstanceManager> _logger;

    public SingleInstanceManager(ILogger<SingleInstanceManager> logger) => _logger = logger;

    public Result<IDisposable> TryAcquire(string key)
    {
        var name = BuildMutexName(key);

        try
        {
            var mutex = new Mutex(initiallyOwned: false, name, out _);

            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died without releasing. We now hold the lock, and state
                // is repaired separately, so carrying on is correct.
                _logger.LogWarning("Took over an abandoned Phoenix lock for {Key}.", key);
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return PhoenixError.Installation(
                    "PX-ALREADY-RUNNING",
                    $"Another Phoenix instance already holds the lock for '{key}'.",
                    requiresRollback: false) with
                {
                    ExitCode = ExitCode.AlreadyRunning,
                };
            }

            _logger.LogDebug("Acquired the single-instance lock {Name}.", name);
            return Result<IDisposable>.Success(new MutexLock(mutex));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            // A machine that refuses named mutexes should not stop the installer entirely;
            // it only loses the cross-process guard, which is logged loudly.
            _logger.LogWarning(ex, "Could not create the single-instance lock; continuing without it.");
            return Result<IDisposable>.Success(new NoOpLock());
        }
    }

    private static string BuildMutexName(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
        var suffix = Convert.ToHexString(hash)[..16];

        // Local\ rather than Global\: no elevation needed, and per-session isolation is
        // the right scope for a per-user installation.
        return $"Local\\Phoenix-{suffix}";
    }

    private sealed class MutexLock : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _disposed;

        public MutexLock(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owner any more; nothing useful to do.
            }

            _mutex.Dispose();
        }
    }

    private sealed class NoOpLock : IDisposable
    {
        public void Dispose()
        {
            // Nothing was acquired.
        }
    }
}
