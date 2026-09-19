using System.Net;
using System.Net.Sockets;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Errors;
using Phoenix.Infrastructure.Downloads;
using Phoenix.Infrastructure.Health;
using Phoenix.Infrastructure.Net;
using Phoenix.Tests.TestSupport;

namespace Phoenix.Tests;

public class TransientFailureTests
{
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void Classifies_status_codes(HttpStatusCode status, bool expected) =>
        Assert.Equal(expected, TransientFailure.IsTransient(status));

    [Fact]
    public void A_name_that_does_not_resolve_is_not_transient() =>
        Assert.False(TransientFailure.IsTransient(new SocketException((int)SocketError.HostNotFound)));

    [Fact]
    public void A_dropped_connection_is_transient() =>
        Assert.True(TransientFailure.IsTransient(new SocketException((int)SocketError.ConnectionReset)));

    [Fact]
    public void A_timeout_is_transient() =>
        Assert.True(TransientFailure.IsTransient(new TaskCanceledException()));

    [Fact]
    public void An_unrelated_exception_is_not_transient() =>
        Assert.False(TransientFailure.IsTransient(new InvalidOperationException()));

    [Fact]
    public void Backoff_grows_and_stays_bounded()
    {
        var first = TransientFailure.BackoffFor(0, 500);
        var later = TransientFailure.BackoffFor(4, 500);
        var extreme = TransientFailure.BackoffFor(50, 500);

        Assert.True(first < later);
        Assert.True(extreme <= TimeSpan.FromSeconds(30));
    }
}

public class DownloadManagerTests
{
    private static DownloadManager Create(
        TempDirectory temp,
        HttpMessageHandler handler,
        Action<Phoenix.Core.Configuration.PhoenixOptions>? configure = null)
    {
        var options = TestFactory.CreateOptions(temp.Path, configure);
        return new DownloadManager(
            new FakeHttpClientFactory(handler),
            TestFactory.Wrap(options),
            TestFactory.CreateFaultInjector(options),
            TestFactory.Logger<DownloadManager>());
    }

    [Fact]
    public async Task Refuses_plain_http_for_a_remote_host()
    {
        using var temp = new TempDirectory("dl-http");
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.OK, "payload");

        var result = await Create(temp, handler).DownloadToFileAsync(
            DownloadDescriptor.For(new Uri("http://example.invalid/package.zip")),
            temp.Combine("package.zip"),
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-DL-INSECURE", result.Error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Refuses_a_host_that_is_not_on_the_allow_list()
    {
        using var temp = new TempDirectory("dl-host");
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.OK, "payload");

        var result = await Create(temp, handler).DownloadToFileAsync(
            DownloadDescriptor.For(new Uri("https://evil.invalid/package.zip")),
            temp.Combine("package.zip"),
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-DL-HOST", result.Error.Code);
        Assert.Equal(ErrorCategory.Security, result.Error.Category);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Allows_a_loopback_host_over_plain_http()
    {
        using var temp = new TempDirectory("dl-loopback");
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.OK, "payload");

        var destination = temp.Combine("package.zip");
        var result = await Create(temp, handler).DownloadToFileAsync(
            DownloadDescriptor.For(new Uri("http://localhost:8099/package.zip")),
            destination,
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("payload", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Leaves_no_partial_file_behind_when_a_download_fails()
    {
        using var temp = new TempDirectory("dl-partial");
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.NotFound);

        var destination = temp.Combine("package.zip");
        var result = await Create(temp, handler, o => o.Network.MaxRetries = 0).DownloadToFileAsync(
            DownloadDescriptor.For(new Uri("https://github.com/package.zip")),
            destination,
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task Retries_a_transient_failure_and_then_succeeds()
    {
        using var temp = new TempDirectory("dl-retry");
        var handler = new FakeHttpMessageHandler((_, call) => call < 3
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("payload") });

        var result = await Create(
                temp,
                handler,
                o =>
                {
                    o.Network.MaxRetries = 3;
                    o.Network.RetryBaseDelayMilliseconds = 1;
                })
            .DownloadToFileAsync(
                DownloadDescriptor.For(new Uri("https://github.com/package.zip")),
                temp.Combine("package.zip"),
                progress: null,
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Does_not_retry_a_permanent_failure()
    {
        using var temp = new TempDirectory("dl-no-retry");
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.Unauthorized);

        var result = await Create(
                temp,
                handler,
                o =>
                {
                    o.Network.MaxRetries = 3;
                    o.Network.RetryBaseDelayMilliseconds = 1;
                })
            .DownloadToFileAsync(
                DownloadDescriptor.For(new Uri("https://github.com/package.zip")),
                temp.Combine("package.zip"),
                progress: null,
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Refuses_a_response_larger_than_the_configured_maximum()
    {
        using var temp = new TempDirectory("dl-too-big");
        var content = new StringContent(new string('x', 4096));
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var result = await Create(temp, handler, o => o.Security.MaxPackageSizeBytes = 512)
            .DownloadToFileAsync(
                DownloadDescriptor.For(new Uri("https://github.com/package.zip")),
                temp.Combine("package.zip"),
                progress: null,
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-DL-TOO-LARGE", result.Error.Code);
    }

    [Fact]
    public async Task Reports_progress_while_downloading()
    {
        using var temp = new TempDirectory("dl-progress");
        var payload = new string('x', 300_000);
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });

        var reports = new List<long>();
        var progress = new Progress<Phoenix.Core.Models.DownloadProgress>(p => reports.Add(p.BytesTransferred));

        var result = await Create(temp, handler).DownloadToFileAsync(
            DownloadDescriptor.For(new Uri("https://github.com/package.zip")),
            temp.Combine("package.zip"),
            progress,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Progress is asynchronous; give the synchronization context a moment to drain.
        await Task.Delay(100);
        Assert.NotEmpty(reports);
    }

    [Fact]
    public async Task Downloads_small_text_within_the_configured_bound()
    {
        using var temp = new TempDirectory("dl-text");
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.OK, "{\"schemaVersion\":1}");

        var result = await Create(temp, handler).DownloadTextAsync(
            DownloadDescriptor.For(new Uri("https://github.com/release-manifest.json")),
            maxBytes: 1024,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("schemaVersion", result.Value, StringComparison.Ordinal);
    }
}

public class HealthCheckServiceTests
{
    private static HealthCheckService Create(
        HttpMessageHandler handler,
        Action<Phoenix.Core.Configuration.PhoenixOptions>? configure = null)
    {
        var options = TestFactory.CreateOptions(Path.GetTempPath(), o =>
        {
            o.HealthCheck.MaxAttempts = 3;
            o.HealthCheck.RetryIntervalMilliseconds = 10;
            o.HealthCheck.OverallTimeoutSeconds = 5;
            configure?.Invoke(o);
        });

        return new HealthCheckService(
            new FakeHttpClientFactory(handler),
            TestFactory.Wrap(options),
            TestFactory.Logger<HealthCheckService>());
    }

    [Fact]
    public async Task Succeeds_as_soon_as_the_endpoint_answers()
    {
        var handler = new FakeHttpMessageHandler((_, call) => call < 2
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Create(handler).WaitForHealthyAsync(
            new Uri("http://localhost:5080/health"),
            process: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Gives_up_after_the_configured_number_of_attempts()
    {
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);

        var result = await Create(handler).WaitForHealthyAsync(
            new Uri("http://localhost:5080/health"),
            process: null,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-HEALTH-TIMEOUT", result.Error.Code);
        Assert.True(result.Error.RequiresRollback);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Stops_immediately_when_the_application_has_already_exited()
    {
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.OK);

        var result = await Create(handler).WaitForHealthyAsync(
            new Uri("http://localhost:5080/health"),
            new ExitedProcess(exitCode: 3),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-HEALTH-PROCESS-EXITED", result.Error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(handler).WaitForHealthyAsync(
                new Uri("http://localhost:5080/health"),
                process: null,
                cancellation.Token));
    }

    private sealed class ExitedProcess : IApplicationProcess
    {
        public ExitedProcess(int exitCode) => ExitCode = exitCode;

        public int ProcessId => 1234;

        public bool HasExited => true;

        public int? ExitCode { get; }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
