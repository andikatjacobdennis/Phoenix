using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Diagnostics;
using Phoenix.Core.Orchestration;
using Phoenix.Core.Releases;
using Phoenix.Infrastructure.Downloads;
using Phoenix.Infrastructure.GitHub;
using Phoenix.Infrastructure.Health;
using Phoenix.Infrastructure.Installation;
using Phoenix.Infrastructure.Packaging;
using Phoenix.Infrastructure.Prerequisites;
using Phoenix.Infrastructure.Prerequisites.Detectors;
using Phoenix.Infrastructure.Processes;
using Phoenix.Infrastructure.Security;
using Phoenix.Infrastructure.State;
using Phoenix.Infrastructure.Platform;
using Phoenix.Infrastructure.Testing;
using Phoenix.Infrastructure.Ui;
using Spectre.Console;

namespace Phoenix.Infrastructure.DependencyInjection;

public static class PhoenixServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything Phoenix needs. One call, so a host - or a test - gets a complete,
    /// consistent object graph rather than a hand-assembled subset.
    /// </summary>
    public static IServiceCollection AddPhoenix(
        this IServiceCollection services,
        IConfiguration configuration,
        string environmentName,
        PhoenixRunOptions runOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(runOptions);

        services.AddSingleton(runOptions);

        services
            .AddOptions<PhoenixOptions>()
            .Bind(configuration.GetSection(PhoenixOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<PhoenixOptions>>(
            new PhoenixOptionsValidator(environmentName));

        services.AddSingleton(sp =>
            PhoenixPaths.Create(sp.GetRequiredService<IOptions<PhoenixOptions>>().Value, environmentName));

        services.AddSingleton<IAnsiConsole>(AnsiConsole.Console);
        services.AddSingleton<IUserInterface, SpectreUserInterface>();

        services.AddSingleton<IEnvironmentService>(sp => new EnvironmentService(
            environmentName,
            sp.GetRequiredService<ILogger<EnvironmentService>>()));

        services.AddSingleton<FaultInjector>();
        services.AddSingleton<ReleaseSelector>();

        services.AddSingleton<IStateStore, JsonStateStore>();
        services.AddSingleton<IDiskSpaceService, DiskSpaceService>();
        services.AddSingleton<ISingleInstanceManager, SingleInstanceManager>();
        services.AddSingleton<IBrowserLauncher, BrowserLauncher>();

        services.AddSingleton<IReleaseProvider, GitHubReleaseProvider>();
        services.AddSingleton<IDownloadManager, DownloadManager>();
        services.AddSingleton<IPackageVerifier, PackageVerifier>();
        services.AddSingleton<IPackageExtractor, ZipPackageExtractor>();

        services.AddSingleton<IReleaseInstaller, ReleaseInstaller>();
        services.AddSingleton<IInstallationManager, InstallationManager>();
        services.AddSingleton<ICleanupService, CleanupService>();

        services.AddSingleton<IApplicationLauncher, ApplicationLauncher>();
        services.AddSingleton<IHealthCheckService, HealthCheckService>();

        services.AddSingleton<CommandVersionDetector>();
        services.AddSingleton<IPrerequisiteDetector, DotNetRuntimeDetector>();
        services.AddSingleton<IPrerequisiteDetector>(sp => sp.GetRequiredService<CommandVersionDetector>());
        services.AddSingleton<IPrerequisiteDetector, ArbitraryCommandDetector>();
        services.AddSingleton<IPrerequisiteDetector, FileVersionDetector>();
        services.AddSingleton<IPrerequisiteDetector, RegistryDetector>();
        services.AddSingleton<IPrerequisiteDetector, AssumeMissingDetector>();
        services.AddSingleton<IPrerequisiteDetector, AssumePresentDetector>();
        services.AddSingleton<PrerequisiteInstaller>();
        services.AddSingleton<IPrerequisiteManager, PrerequisiteManager>();

        services.AddSingleton<PhoenixOrchestrator>();

        AddHttpClients(services);

        return services;
    }

    private static void AddHttpClients(IServiceCollection services)
    {
        var userAgent = BuildUserAgent();

        void Configure(HttpClient client, PhoenixOptions options, bool githubHeaders)
        {
            client.Timeout = TimeSpan.FromSeconds(options.Network.RequestTimeoutSeconds);

            var agent = string.IsNullOrWhiteSpace(options.Network.UserAgentSuffix)
                ? userAgent
                : $"{userAgent} ({options.Network.UserAgentSuffix})";

            client.DefaultRequestHeaders.UserAgent.ParseAdd(agent);

            if (githubHeaders)
            {
                client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            }
        }

        services.AddHttpClient(GitHubReleaseProvider.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
                Configure(client, sp.GetRequiredService<IOptions<PhoenixOptions>>().Value, githubHeaders: true));

        services.AddHttpClient(DownloadManager.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
                Configure(client, sp.GetRequiredService<IOptions<PhoenixOptions>>().Value, githubHeaders: false));

        services.AddHttpClient(HealthCheckService.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
                Configure(client, sp.GetRequiredService<IOptions<PhoenixOptions>>().Value, githubHeaders: false));
    }

    /// <summary>A user agent that identifies Phoenix and its version, as the GitHub API expects.</summary>
    private static string BuildUserAgent() => $"Phoenix/{PhoenixVersionInfo.DisplayVersion}";
}
