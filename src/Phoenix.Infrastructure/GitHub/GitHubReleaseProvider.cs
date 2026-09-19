using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Core.Versioning;
using Phoenix.Infrastructure.Net;

namespace Phoenix.Infrastructure.GitHub;

/// <summary>
/// Reads releases from the GitHub Releases API. All GitHub-specific knowledge - headers,
/// pagination, rate limiting, asset URLs - is confined to this class.
/// </summary>
public sealed class GitHubReleaseProvider : IReleaseProvider
{
    public const string HttpClientName = "phoenix-github";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PhoenixOptions _options;
    private readonly ILogger<GitHubReleaseProvider> _logger;

    public GitHubReleaseProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<PhoenixOptions> options,
        ILogger<GitHubReleaseProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<ReleaseInfo>>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        var url =
            $"{_options.GitHub.ApiBaseUrl.TrimEnd('/')}/repos/{_options.GitHub.Owner}/{_options.GitHub.Repository}" +
            $"/releases?per_page={_options.GitHub.MaxReleasesToInspect.ToString(CultureInfo.InvariantCulture)}";

        var maxAttempts = Math.Max(1, _options.Network.MaxRetries + 1);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await TryGetReleasesAsync(url, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess || !result.Error.IsRetryable || attempt == maxAttempts - 1)
            {
                return result;
            }

            var delay = TransientFailure.BackoffFor(attempt, _options.Network.RetryBaseDelayMilliseconds);
            _logger.LogWarning(
                "GitHub release lookup attempt {Attempt} failed ({Code}); retrying in {Delay}.",
                attempt + 1,
                result.Error.Code,
                delay);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return PhoenixError.GitHub("PX-GH-RETRIES", "Release lookup did not succeed within the retry budget.");
    }

    private async Task<Result<IReadOnlyList<ReleaseInfo>>> TryGetReleasesAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return TranslateFailure(response);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await JsonSerializer
                .DeserializeAsync<List<GitHubRelease>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (payload is null)
            {
                return PhoenixError.GitHub("PX-GH-EMPTY", "GitHub returned an empty release list document.");
            }

            var releases = new List<ReleaseInfo>(payload.Count);

            foreach (var release in payload)
            {
                if (release.Draft && !_options.GitHub.IncludeDrafts)
                {
                    continue;
                }

                if (!SemanticVersion.TryParse(release.TagName, out var version))
                {
                    _logger.LogDebug("Ignoring release '{Tag}': not a semantic version.", release.TagName);
                    continue;
                }

                releases.Add(new ReleaseInfo
                {
                    Tag = release.TagName ?? version.ToString(),
                    Version = version,
                    Name = release.Name,
                    IsPrerelease = release.Prerelease,
                    IsDraft = release.Draft,
                    PublishedAt = release.PublishedAt,
                    Assets = release.Assets
                        .Where(a => !string.IsNullOrWhiteSpace(a.Name) && a.BrowserDownloadUrl is not null)
                        .Select(a => new ReleaseAsset
                        {
                            Name = a.Name!,
                            DownloadUrl = a.BrowserDownloadUrl!,
                            ApiUrl = a.Url,
                            SizeBytes = a.Size,
                            ContentType = a.ContentType,
                        })
                        .ToList(),
                });
            }

            _logger.LogInformation(
                "Found {Count} usable releases in {Owner}/{Repository}.",
                releases.Count,
                _options.GitHub.Owner,
                _options.GitHub.Repository);

            return Result<IReadOnlyList<ReleaseInfo>>.Success(releases);
        }
        catch (JsonException ex)
        {
            return PhoenixError.GitHub("PX-GH-MALFORMED", $"GitHub returned a malformed response: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PhoenixError.Network("PX-GH-TIMEOUT", $"The request to {url} timed out.", retryable: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return PhoenixError.Network(
                "PX-GH-NETWORK",
                $"Could not reach {url}: {ex.Message}",
                ex,
                TransientFailure.IsTransient(ex));
        }
    }

    private Result<IReadOnlyList<ReleaseInfo>> TranslateFailure(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var rateLimitRemaining = FirstHeaderValue(response, "x-ratelimit-remaining");

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests &&
            rateLimitRemaining == "0")
        {
            var reset = FirstHeaderValue(response, "x-ratelimit-reset");
            return new PhoenixError
            {
                Category = ErrorCategory.GitHub,
                Code = "PX-GH-RATELIMIT",
                UserMessage = "The update service is busy. Please try again in a few minutes.",
                TechnicalMessage = $"GitHub rate limit exhausted (reset at unix time {reset ?? "unknown"}).",
                IsRetryable = false,
                ExitCode = ExitCode.InstallationFailure,
            };
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return PhoenixError.GitHub(
                "PX-GH-AUTH",
                $"GitHub refused the request with {status}. If the repository is private, set the " +
                $"{_options.GitHub.TokenEnvironmentVariable} environment variable to a token with 'contents: read'.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return PhoenixError.GitHub(
                "PX-GH-NOTFOUND",
                $"Repository {_options.GitHub.Owner}/{_options.GitHub.Repository} was not found (404).");
        }

        return PhoenixError.GitHub(
            "PX-GH-STATUS",
            $"GitHub returned {status} {response.ReasonPhrase}.",
            retryable: TransientFailure.IsTransient(response.StatusCode));
    }

    private static string? FirstHeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    public DownloadDescriptor DescribeAsset(ReleaseAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var token = ReadToken();

        // Private repositories only serve assets through the API endpoint, and only with a
        // token. The asset URL comes from a remote response, so the token is attached only
        // when that URL is on the host we were configured to talk to - a tampered response
        // must not be able to redirect credentials somewhere else.
        if (token is not null && asset.ApiUrl is not null && IsConfiguredApiHost(asset.ApiUrl))
        {
            headers["Accept"] = "application/octet-stream";
            headers["Authorization"] = $"Bearer {token}";
            headers["X-GitHub-Api-Version"] = "2022-11-28";
            return new DownloadDescriptor(asset.ApiUrl, headers, asset.SizeBytes > 0 ? asset.SizeBytes : null);
        }

        return new DownloadDescriptor(asset.DownloadUrl, headers, asset.SizeBytes > 0 ? asset.SizeBytes : null);
    }

    private bool IsConfiguredApiHost(Uri url)
    {
        if (!Uri.TryCreate(_options.GitHub.ApiBaseUrl, UriKind.Absolute, out var configured))
        {
            return false;
        }

        if (!string.Equals(url.Host, configured.Host, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "An asset URL points at {Host} rather than the configured {ConfiguredHost}; " +
                "no credentials will be sent with it.",
                url.Host,
                configured.Host);

            return false;
        }

        return true;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var token = ReadToken();

        if (token is not null)
        {
            // Set per request rather than on the shared handler, so the token never
            // outlives the call and never reaches a different host.
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private string? ReadToken()
    {
        var name = _options.GitHub.TokenEnvironmentVariable;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var token = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
