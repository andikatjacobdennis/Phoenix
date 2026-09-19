using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Configuration;

namespace Phoenix.Infrastructure.Testing;

/// <summary>
/// Lets a Development configuration break Phoenix on purpose, so the recovery paths can be
/// exercised for real rather than only in unit tests.
///
/// Configuration validation refuses any fault outside the Development environment, and every
/// injection point is explicit and logged - there is no hidden behaviour in production.
/// </summary>
public sealed class FaultInjector
{
    /// <summary>
    /// Environment variable the demo application reads to simulate a crash or an unhealthy
    /// state. Real applications ignore it.
    /// </summary>
    public const string ChildSimulationVariable = "PHOENIX_DEMO_SIMULATE";

    private readonly SimulatedFault _fault;
    private readonly ILogger<FaultInjector> _logger;

    public FaultInjector(IOptions<PhoenixOptions> options, ILogger<FaultInjector> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _fault = options.Value.Testing.SimulateFault;
        _logger = logger;

        if (_fault != SimulatedFault.None)
        {
            _logger.LogWarning("Fault injection is active: {Fault}.", _fault);
        }
    }

    public SimulatedFault Fault => _fault;

    public bool IsActive => _fault != SimulatedFault.None;

    public void ThrowIfDownloadShouldFail()
    {
        if (_fault == SimulatedFault.DownloadFailure)
        {
            _logger.LogWarning("Injecting a download failure.");
            throw new HttpRequestException("Simulated download failure (Phoenix fault injection).");
        }
    }

    public void ThrowIfDownloadShouldBeInterrupted(long transferred, long? total)
    {
        if (_fault != SimulatedFault.InterruptedDownload || total is not > 0)
        {
            return;
        }

        if (transferred >= total / 2)
        {
            _logger.LogWarning("Injecting an interrupted download at {Transferred} bytes.", transferred);
            throw new IOException("Simulated connection reset (Phoenix fault injection).");
        }
    }

    /// <summary>When true, the verifier reports a checksum mismatch even for an intact file.</summary>
    public bool ShouldFailChecksum => _fault == SimulatedFault.ChecksumMismatch;

    /// <summary>When true, the extractor behaves as if the archive were corrupt.</summary>
    public bool ShouldCorruptArchive => _fault == SimulatedFault.CorruptArchive;

    /// <summary>When true, activation fails after the candidate has been prepared.</summary>
    public bool ShouldFailActivation => _fault == SimulatedFault.ActivationFailure;

    /// <summary>
    /// The value passed to the child process so it misbehaves in a realistic way: the
    /// application really does crash or really does report unhealthy.
    /// </summary>
    public string? ChildProcessSimulation => _fault switch
    {
        SimulatedFault.StartupFailure => "crash",
        SimulatedFault.HealthCheckFailure => "unhealthy",
        _ => null,
    };
}
