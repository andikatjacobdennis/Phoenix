using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Phoenix.IntegrationTests.Support;

/// <summary>
/// A real HTTP server that speaks just enough of the GitHub Releases API for Phoenix.
///
/// Using Kestrel rather than a stubbed handler means the integration tests exercise the whole
/// path: HttpClient, headers, streaming downloads, content lengths and asset URLs.
/// </summary>
public sealed class FakeGitHubServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ReleaseOrigin _origin;

    private FakeGitHubServer(WebApplication app, ReleaseOrigin origin, Uri baseAddress)
    {
        _app = app;
        _origin = origin;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    /// <summary>Set to a status code to make the next release lookups fail, or null to serve normally.</summary>
    public HttpStatusCode? ForcedStatusCode { get; set; }

    public int ReleaseRequests { get; private set; }

    public int AssetRequests { get; private set; }

    public static async Task<FakeGitHubServer> StartAsync(ReleaseOrigin origin, string owner, string repository)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();

        FakeGitHubServer? server = null;

        app.MapGet($"/repos/{owner}/{repository}/releases", (HttpContext context) =>
        {
            server!.ReleaseRequests++;

            if (server.ForcedStatusCode is { } status)
            {
                return Results.StatusCode((int)status);
            }

            var releases = server._origin.BuildReleasePayload(server.BaseAddress);
            return Results.Text(
                JsonSerializer.Serialize(releases),
                "application/json");
        });

        app.MapGet("/assets/{version}/{file}", (string version, string file) =>
        {
            server!.AssetRequests++;

            var path = server._origin.ResolveAsset(version, file);
            return path is null
                ? Results.NotFound()
                : Results.File(path, "application/octet-stream");
        });

        await app.StartAsync();

        var address = app.Urls.First();
        server = new FakeGitHubServer(app, origin, new Uri(address));
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
