using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Errors;

namespace Phoenix.Infrastructure.Health;

/// <summary>
/// Polls the application's health endpoint until it answers, or until the budget runs out.
///
/// The budget is always bounded - attempts, interval and an overall timeout - because an
/// unbounded health check is indistinguishable from a hang. If the process dies while we are
/// polling, the wait ends immediately: there is nothing left to become healthy.
/// </summary>
public sealed class HealthCheckService : IHealthCheckService
{
    public const string HttpClientName = "phoenix-health";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HealthCheckOptions _options;
    private readonly ILogger<HealthCheckService> _logger;

    public HealthCheckService(
        IHttpClientFactory httpClientFactory,
        IOptions<PhoenixOptions> options,
        ILogger<HealthCheckService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _httpClientFactory = httpClientFactory;
        _options = options.Value.HealthCheck;
        _logger = logger;
    }

    public async Task<Result> WaitForHealthyAsync(
        Uri healthUrl,
        IApplicationProcess? process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(healthUrl);

        using var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);

        var overall = TimeSpan.FromSeconds(_options.OverallTimeoutSeconds);
        var interval = TimeSpan.FromMilliseconds(Math.Max(100, _options.RetryIntervalMilliseconds));
        var stopwatch = Stopwatch.StartNew();

        string? lastFailure = null;

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process is { HasExited: true })
            {
                return PhoenixError.Startup(
                    "PX-HEALTH-PROCESS-EXITED",
                    $"The application exited with code {process.ExitCode?.ToString() ?? "unknown"} " +
                    $"before it became healthy (after {stopwatch.Elapsed.TotalSeconds:0.#}s).");
            }

            if (stopwatch.Elapsed > overall)
            {
                break;
            }

            var attemptResult = await ProbeAsync(client, healthUrl, cancellationToken).ConfigureAwait(false);

            if (attemptResult is null)
            {
                _logger.LogInformation(
                    "Health check succeeded for {Url} after {Attempts} attempt(s) in {Elapsed}s.",
                    healthUrl,
                    attempt,
                    stopwatch.Elapsed.TotalSeconds.ToString("0.#"));

                return Result.Success();
            }

            lastFailure = attemptResult;
            _logger.LogDebug(
                "Health check attempt {Attempt} for {Url} failed: {Reason}",
                attempt,
                healthUrl,
                attemptResult);

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return PhoenixError.HealthCheck(
            "PX-HEALTH-TIMEOUT",
            $"{healthUrl} did not report healthy within {stopwatch.Elapsed.TotalSeconds:0.#}s " +
            $"({_options.MaxAttempts} attempts allowed). Last failure: {lastFailure ?? "none recorded"}.");
    }

    /// <summary>Returns null when healthy, or a short description of why it was not.</summary>
    private async Task<string?> ProbeAsync(HttpClient client, Uri healthUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(healthUrl, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (_options.ExpectedStatusCodes.Contains(status))
            {
                return null;
            }

            return $"HTTP {status} {response.ReasonPhrase}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "request timed out";
        }
        catch (HttpRequestException ex)
        {
            return ex.Message;
        }
    }
}
