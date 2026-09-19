using System.Text;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Infrastructure.Net;
using Phoenix.Infrastructure.Testing;

namespace Phoenix.Infrastructure.Downloads;

/// <summary>
/// Downloads files safely: the host is checked against the allow-list, the transfer lands in a
/// temporary file, and only a complete transfer is moved into place. A partial download is
/// therefore never mistaken for a package.
/// </summary>
public sealed class DownloadManager : IDownloadManager
{
    public const string HttpClientName = "phoenix-download";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PhoenixOptions _options;
    private readonly FaultInjector _faults;
    private readonly ILogger<DownloadManager> _logger;

    public DownloadManager(
        IHttpClientFactory httpClientFactory,
        IOptions<PhoenixOptions> options,
        FaultInjector faults,
        ILogger<DownloadManager> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _faults = faults;
        _logger = logger;
    }

    public async Task<Result<string>> DownloadToFileAsync(
        DownloadDescriptor descriptor,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var allowed = EnsureHostAllowed(descriptor.Url);
        if (allowed.IsFailure)
        {
            return Result<string>.Failure(allowed.Error);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var maxAttempts = Math.Max(1, _options.Network.MaxRetries + 1);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var partialPath = destinationPath + ".part";
            var result = await TryDownloadAsync(descriptor, partialPath, progress, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                // Move into place only now: everything before this point is disposable.
                File.Move(partialPath, destinationPath, overwrite: true);
                return Result<string>.Success(destinationPath);
            }

            TryDelete(partialPath);

            if (!result.Error.IsRetryable || attempt == maxAttempts - 1)
            {
                return Result<string>.Failure(result.Error);
            }

            var delay = TransientFailure.BackoffFor(attempt, _options.Network.RetryBaseDelayMilliseconds);
            _logger.LogWarning(
                "Download attempt {Attempt} of {Url} failed ({Code}); retrying in {Delay}.",
                attempt + 1,
                descriptor.Url,
                result.Error.Code,
                delay);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return PhoenixError.Download("PX-DL-RETRIES", $"Downloading {descriptor.Url} exhausted the retry budget.");
    }

    private async Task<Result> TryDownloadAsync(
        DownloadDescriptor descriptor,
        string partialPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            _faults.ThrowIfDownloadShouldFail();

            using var client = _httpClientFactory.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromMinutes(_options.Network.DownloadTimeoutMinutes);

            using var request = BuildRequest(descriptor);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return PhoenixError.Download(
                    "PX-DL-STATUS",
                    $"{descriptor.Url} returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                    retryable: TransientFailure.IsTransient(response.StatusCode));
            }

            var contentLength = response.Content.Headers.ContentLength ?? descriptor.ExpectedSizeBytes;

            if (contentLength > _options.Security.MaxPackageSizeBytes)
            {
                return PhoenixError.Security(
                    "PX-DL-TOO-LARGE",
                    $"{descriptor.Url} advertises {contentLength} bytes, which exceeds the configured " +
                    $"maximum of {_options.Security.MaxPackageSizeBytes}.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            var buffer = new byte[81920];
            long transferred = 0;
            var lastReport = 0L;

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                transferred += read;

                if (transferred > _options.Security.MaxPackageSizeBytes)
                {
                    return PhoenixError.Security(
                        "PX-DL-TOO-LARGE",
                        $"{descriptor.Url} exceeded the configured maximum package size while downloading.");
                }

                _faults.ThrowIfDownloadShouldBeInterrupted(transferred, contentLength);

                // Report at most every 64 KiB so a fast transfer does not flood the console.
                if (progress is not null && transferred - lastReport >= 65536)
                {
                    lastReport = transferred;
                    progress.Report(new DownloadProgress(transferred, contentLength));
                }
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(transferred, contentLength ?? transferred));

            if (contentLength is > 0 && transferred != contentLength)
            {
                return PhoenixError.Download(
                    "PX-DL-TRUNCATED",
                    $"{descriptor.Url} delivered {transferred} of {contentLength} bytes.");
            }

            _logger.LogInformation(
                "Downloaded {Bytes} bytes from {Url}.",
                transferred.ToString(CultureInfo.InvariantCulture),
                descriptor.Url);

            return Result.Success();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PhoenixError.Download("PX-DL-TIMEOUT", $"Downloading {descriptor.Url} timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return PhoenixError.Download(
                "PX-DL-IO",
                $"Downloading {descriptor.Url} failed: {ex.Message}",
                ex,
                TransientFailure.IsTransient(ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            return PhoenixError.Permission(
                "PX-DL-DENIED",
                $"Writing '{partialPath}' was denied: {ex.Message}",
                ex);
        }
    }

    public async Task<Result<string>> DownloadTextAsync(
        DownloadDescriptor descriptor,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var allowed = EnsureHostAllowed(descriptor.Url);
        if (allowed.IsFailure)
        {
            return Result<string>.Failure(allowed.Error);
        }

        var maxAttempts = Math.Max(1, _options.Network.MaxRetries + 1);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await TryDownloadTextAsync(descriptor, maxBytes, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess || !result.Error.IsRetryable || attempt == maxAttempts - 1)
            {
                return result;
            }

            await Task.Delay(
                    TransientFailure.BackoffFor(attempt, _options.Network.RetryBaseDelayMilliseconds),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return PhoenixError.Download("PX-DL-RETRIES", $"Downloading {descriptor.Url} exhausted the retry budget.");
    }

    private async Task<Result<string>> TryDownloadTextAsync(
        DownloadDescriptor descriptor,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = BuildRequest(descriptor);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return PhoenixError.Download(
                    "PX-DL-STATUS",
                    $"{descriptor.Url} returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                    retryable: TransientFailure.IsTransient(response.StatusCode));
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                return PhoenixError.Security(
                    "PX-DL-TEXT-TOO-LARGE",
                    $"{descriptor.Url} is {response.Content.Headers.ContentLength} bytes, over the {maxBytes} byte limit.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            var buffer = new char[4096];
            var builder = new StringBuilder();

            while (true)
            {
                var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                builder.Append(buffer, 0, read);

                if (builder.Length > maxBytes)
                {
                    return PhoenixError.Security(
                        "PX-DL-TEXT-TOO-LARGE",
                        $"{descriptor.Url} exceeded the {maxBytes} byte limit while downloading.");
                }
            }

            return Result<string>.Success(builder.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PhoenixError.Download("PX-DL-TIMEOUT", $"Downloading {descriptor.Url} timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return PhoenixError.Download(
                "PX-DL-IO",
                $"Downloading {descriptor.Url} failed: {ex.Message}",
                ex,
                TransientFailure.IsTransient(ex));
        }
    }

    private static HttpRequestMessage BuildRequest(DownloadDescriptor descriptor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, descriptor.Url);

        foreach (var (name, value) in descriptor.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    /// <summary>
    /// Refuses plain HTTP and hosts outside the allow-list. This is what stops a tampered
    /// manifest from redirecting a download to somebody else's server.
    /// </summary>
    private Result EnsureHostAllowed(Uri url)
    {
        if (_options.Security.RequireHttps &&
            !url.IsLoopback &&
            !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return PhoenixError.Security(
                "PX-DL-INSECURE",
                $"Refusing to download from '{url}': HTTPS is required.");
        }

        var allowList = _options.Security.AllowedDownloadHosts;
        if (allowList.Length == 0 || url.IsLoopback)
        {
            return Result.Success();
        }

        foreach (var host in allowList)
        {
            if (string.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase) ||
                url.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Success();
            }
        }

        return PhoenixError.Security(
            "PX-DL-HOST",
            $"Refusing to download from '{url.Host}': it is not in Security:AllowedDownloadHosts.");
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not remove partial download {Path}.", path);
        }
    }
}
