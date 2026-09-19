using System.Net;
using Phoenix.Core.Models;
using Phoenix.Infrastructure.GitHub;
using Phoenix.Tests.TestSupport;

namespace Phoenix.Tests;

public class GitHubReleaseProviderTests
{
    private const string ReleasesJson = """
        [
          {
            "tag_name": "v1.2.0",
            "name": "Demo 1.2.0",
            "draft": false,
            "prerelease": false,
            "published_at": "2026-01-15T10:00:00Z",
            "assets": [
              {
                "name": "release-manifest.json",
                "size": 512,
                "content_type": "application/json",
                "browser_download_url": "https://github.com/o/r/releases/download/v1.2.0/release-manifest.json",
                "url": "https://api.github.com/repos/o/r/releases/assets/1"
              },
              {
                "name": "phoenix-demoapp-win-x64.zip",
                "size": 1048576,
                "content_type": "application/zip",
                "browser_download_url": "https://github.com/o/r/releases/download/v1.2.0/phoenix-demoapp-win-x64.zip",
                "url": "https://api.github.com/repos/o/r/releases/assets/2"
              }
            ]
          },
          {
            "tag_name": "v1.3.0-qa.1",
            "draft": false,
            "prerelease": true,
            "published_at": "2026-01-20T10:00:00Z",
            "assets": []
          },
          {
            "tag_name": "v9.9.9",
            "draft": true,
            "prerelease": false,
            "assets": []
          },
          {
            "tag_name": "nightly",
            "draft": false,
            "prerelease": false,
            "assets": []
          }
        ]
        """;

    private static GitHubReleaseProvider Create(
        HttpMessageHandler handler,
        Action<Phoenix.Core.Configuration.PhoenixOptions>? configure = null)
    {
        var options = TestFactory.CreateOptions(Path.GetTempPath(), configure);
        return new GitHubReleaseProvider(
            new FakeHttpClientFactory(handler),
            TestFactory.Wrap(options),
            TestFactory.Logger<GitHubReleaseProvider>());
    }

    [Fact]
    public async Task Parses_releases_and_ignores_drafts_and_non_versions()
    {
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.OK, ReleasesJson);

        var result = await Create(handler).GetReleasesAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["v1.2.0", "v1.3.0-qa.1"], result.Value.Select(r => r.Tag));

        var release = result.Value[0];
        Assert.False(release.IsPrerelease);
        Assert.Equal(2, release.Assets.Count);
        Assert.NotNull(release.FindAsset(ReleaseManifest.DefaultFileName));
        Assert.Equal(1048576, release.FindAsset("phoenix-demoapp-win-x64.zip")!.SizeBytes);
    }

    [Fact]
    public async Task Requests_the_configured_repository()
    {
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.OK, "[]");

        await Create(handler, o =>
        {
            o.GitHub.Owner = "contoso";
            o.GitHub.Repository = "widgets";
            o.GitHub.MaxReleasesToInspect = 7;
        }).GetReleasesAsync(CancellationToken.None);

        var url = handler.Requests.Single().RequestUri!.ToString();
        Assert.Contains("/repos/contoso/widgets/releases", url, StringComparison.Ordinal);
        Assert.Contains("per_page=7", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_rate_limiting_without_retrying_it()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0");
            response.Headers.Add("x-ratelimit-reset", "1780000000");
            return response;
        });

        var result = await Create(handler, o => o.Network.MaxRetries = 3).GetReleasesAsync(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-GH-RATELIMIT", result.Error.Code);
        Assert.False(result.Error.IsRetryable);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("token", result.Error.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explains_an_authentication_failure_by_naming_the_token_variable()
    {
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.Unauthorized);

        var result = await Create(handler, o => o.GitHub.TokenEnvironmentVariable = "CONTOSO_TOKEN")
            .GetReleasesAsync(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-GH-AUTH", result.Error.Code);
        Assert.Contains("CONTOSO_TOKEN", result.Error.TechnicalMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_a_missing_repository()
    {
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.NotFound);

        var result = await Create(handler).GetReleasesAsync(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-GH-NOTFOUND", result.Error.Code);
    }

    [Fact]
    public async Task Retries_a_server_error_and_then_succeeds()
    {
        var handler = new FakeHttpMessageHandler((_, call) => call < 2
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });

        var result = await Create(handler, o =>
        {
            o.Network.MaxRetries = 2;
            o.Network.RetryBaseDelayMilliseconds = 1;
        }).GetReleasesAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Reports_malformed_json_as_a_GitHub_problem()
    {
        var handler = FakeHttpMessageHandler.Always(HttpStatusCode.OK, "{not json");

        var result = await Create(handler).GetReleasesAsync(CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-GH-MALFORMED", result.Error.Code);
    }

    [Fact]
    public void Describes_a_public_asset_without_any_authorization_header()
    {
        var provider = Create(FakeHttpMessageHandler.Always(HttpStatusCode.OK, "[]"));

        var descriptor = provider.DescribeAsset(new ReleaseAsset
        {
            Name = "package.zip",
            DownloadUrl = new Uri("https://github.com/o/r/releases/download/v1/package.zip"),
            ApiUrl = new Uri("https://api.github.com/repos/o/r/releases/assets/2"),
            SizeBytes = 1024,
        });

        Assert.Equal("https://github.com/o/r/releases/download/v1/package.zip", descriptor.Url.ToString());
        Assert.Empty(descriptor.Headers);
        Assert.Equal(1024, descriptor.ExpectedSizeBytes);
    }

    [Fact]
    public void Uses_the_api_url_and_a_token_when_one_is_configured()
    {
        const string variable = "PHOENIX_TEST_GH_TOKEN";
        Environment.SetEnvironmentVariable(variable, "ghp_exampletoken");

        try
        {
            var provider = Create(
                FakeHttpMessageHandler.Always(HttpStatusCode.OK, "[]"),
                o => o.GitHub.TokenEnvironmentVariable = variable);

            var descriptor = provider.DescribeAsset(new ReleaseAsset
            {
                Name = "package.zip",
                DownloadUrl = new Uri("https://github.com/o/r/releases/download/v1/package.zip"),
                ApiUrl = new Uri("https://api.github.com/repos/o/r/releases/assets/2"),
            });

            Assert.Equal("https://api.github.com/repos/o/r/releases/assets/2", descriptor.Url.ToString());
            Assert.Equal("application/octet-stream", descriptor.Headers["Accept"]);
            Assert.StartsWith("Bearer ", descriptor.Headers["Authorization"], StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Never_sends_the_token_to_an_asset_url_on_another_host()
    {
        const string variable = "PHOENIX_TEST_GH_TOKEN";
        Environment.SetEnvironmentVariable(variable, "ghp_exampletoken");

        try
        {
            var provider = Create(
                FakeHttpMessageHandler.Always(HttpStatusCode.OK, "[]"),
                o => o.GitHub.TokenEnvironmentVariable = variable);

            // A tampered release response pointing the API asset URL somewhere else.
            var descriptor = provider.DescribeAsset(new ReleaseAsset
            {
                Name = "package.zip",
                DownloadUrl = new Uri("https://github.com/o/r/releases/download/v1/package.zip"),
                ApiUrl = new Uri("https://evil.invalid/repos/o/r/releases/assets/2"),
            });

            Assert.Equal("https://github.com/o/r/releases/download/v1/package.zip", descriptor.Url.ToString());
            Assert.DoesNotContain("Authorization", descriptor.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
