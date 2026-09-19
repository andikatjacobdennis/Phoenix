using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Diagnostics;
using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Core.Orchestration;
using Phoenix.Infrastructure.DependencyInjection;
using Phoenix.Infrastructure.State;

namespace Phoenix.IntegrationTests.Support;

/// <summary>
/// One simulated machine: its own installation root, its own state, pointed at a fake release
/// server. Each <see cref="RunAsync"/> builds a fresh service provider, which is what a second
/// run of the Phoenix executable would do - state comes back from disk, not from memory.
/// </summary>
public sealed class PhoenixHarness : IAsyncDisposable
{
    private readonly Dictionary<string, string?> _settings;

    public PhoenixHarness(Uri releaseApiBaseAddress, int applicationPort, string environmentName = "Development")
    {
        ArgumentNullException.ThrowIfNull(releaseApiBaseAddress);

        RootPath = Path.Combine(Path.GetTempPath(), "phoenix-tests", $"machine-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);

        EnvironmentName = environmentName;
        ApplicationPort = applicationPort;

        _settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Phoenix:Company:Name"] = "Test Company",
            ["Phoenix:Product:Name"] = "Test Application",
            ["Phoenix:Product:Slug"] = "TestApp",
            ["Phoenix:Application:Id"] = "phoenix-demoapp",
            ["Phoenix:Application:Name"] = "Test Application",
            ["Phoenix:Application:Url"] = $"http://localhost:{applicationPort}",
            ["Phoenix:Application:StartupGraceSeconds"] = "2",
            ["Phoenix:Application:ShutdownTimeoutSeconds"] = "10",
            ["Phoenix:Application:ReuseRunningInstance"] = "false",

            ["Phoenix:GitHub:Owner"] = "test-owner",
            ["Phoenix:GitHub:Repository"] = "test-repo",
            ["Phoenix:GitHub:ApiBaseUrl"] = releaseApiBaseAddress.ToString().TrimEnd('/'),

            ["Phoenix:Directories:Root"] = RootPath,

            ["Phoenix:Updates:Channel"] = "Production",
            ["Phoenix:Updates:Policy"] = "Automatic",
            ["Phoenix:Updates:RemoteFallbackCount"] = "2",

            ["Phoenix:HealthCheck:Enabled"] = "true",
            ["Phoenix:HealthCheck:Path"] = "/health",
            ["Phoenix:HealthCheck:MaxAttempts"] = "30",
            ["Phoenix:HealthCheck:RetryIntervalMilliseconds"] = "250",
            ["Phoenix:HealthCheck:OverallTimeoutSeconds"] = "30",
            ["Phoenix:HealthCheck:RequestTimeoutSeconds"] = "3",

            ["Phoenix:Network:MaxRetries"] = "1",
            ["Phoenix:Network:RetryBaseDelayMilliseconds"] = "10",
            ["Phoenix:Network:RequestTimeoutSeconds"] = "15",

            ["Phoenix:Browser:LaunchOnSuccess"] = "false",
            ["Phoenix:Testing:AllowBrowserLaunch"] = "false",

            // A real detection of the runtime this machine already has: the prerequisite
            // phase is exercised, but nothing is ever installed machine-wide.
            ["Phoenix:Prerequisites:Enabled"] = "true",
            ["Phoenix:Prerequisites:InstallMissing"] = "false",
            ["Phoenix:Prerequisites:Required:0:Id"] = "aspnetcore-runtime",
            ["Phoenix:Prerequisites:Required:0:DisplayName"] = ".NET Runtime",
            ["Phoenix:Prerequisites:Required:0:VersionRange"] = "[9.0,99.0)",
            ["Phoenix:Prerequisites:Required:0:Detection:Strategy"] = "DotNetRuntime",
            ["Phoenix:Prerequisites:Required:0:Detection:RuntimeName"] = "Microsoft.AspNetCore.App",
        };
    }

    public string RootPath { get; }

    public string EnvironmentName { get; }

    public int ApplicationPort { get; }

    public RecordingUserInterface LastUi { get; private set; } = new();

    public PhoenixHarness With(string key, string? value)
    {
        _settings[key] = value;
        return this;
    }

    public PhoenixPaths Paths => PhoenixPaths.Create(BuildOptions(), EnvironmentName);

    private PhoenixOptions BuildOptions()
    {
        var options = new PhoenixOptions();
        BuildConfiguration().GetSection(PhoenixOptions.SectionName).Bind(options);
        return options;
    }

    private IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(_settings).Build();

    /// <summary>Runs Phoenix once, exactly as a fresh process would.</summary>
    public async Task<ExitCode> RunAsync(
        PhoenixRunOptions? run = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = BuildConfiguration();
        var ui = new RecordingUserInterface();
        LastUi = ui;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPhoenix(
            configuration,
            EnvironmentName,
            run ?? new PhoenixRunOptions { OperationId = OperationId.New() });

        // The last registration wins, so the console interface is replaced by the recorder.
        services.AddSingleton<IUserInterface>(ui);

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<PhoenixOrchestrator>();

        return await orchestrator.RunAsync(cancellationToken);
    }

    public async Task<PhoenixState> ReadStateAsync()
    {
        var store = new JsonStateStore(
            Paths,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonStateStore>.Instance);

        return await store.LoadAsync(CancellationToken.None);
    }

    public IReadOnlyList<string> InstalledVersions()
    {
        var versions = Paths.Versions;
        return Directory.Exists(versions)
            ? Directory.GetDirectories(versions).Select(Path.GetFileName).OfType<string>().Order().ToList()
            : [];
    }

    public static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Stops every process started out of this machine's installation directory.</summary>
    public void StopApplications()
    {
        var root = Path.GetFullPath(RootPath);

        foreach (var process in Process.GetProcesses())
        {
            string? path = null;

            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Most processes cannot be inspected; that is expected.
            }

            if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Already gone.
                }
            }

            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopApplications();

        // Give Windows a moment to release the file handles of the process we just killed.
        await Task.Delay(200);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(300 * (attempt + 1));
            }
        }
    }

    public string Describe() =>
        string.Create(CultureInfo.InvariantCulture, $"machine at {RootPath}, port {ApplicationPort}");
}
