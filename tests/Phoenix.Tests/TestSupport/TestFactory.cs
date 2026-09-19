using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Phoenix.Core.Configuration;
using Phoenix.Core.Diagnostics;
using Phoenix.Core.Orchestration;
using Phoenix.Infrastructure.Testing;

namespace Phoenix.Tests.TestSupport;

/// <summary>Builders for the objects most tests need, with sensible, safe defaults.</summary>
public static class TestFactory
{
    public static PhoenixOptions CreateOptions(string root, Action<PhoenixOptions>? configure = null)
    {
        var options = new PhoenixOptions
        {
            Company = { Name = "Test Company" },
            Product = { Name = "Test Product", Slug = "TestProduct" },
            Application =
            {
                Id = "phoenix-demoapp",
                Name = "Test Application",
                Url = "http://localhost:5080",
                StartupGraceSeconds = 0,
            },
            GitHub = { Owner = "test-owner", Repository = "test-repo" },
            Directories = { Root = root },
            Testing = { AllowBrowserLaunch = false },
        };

        configure?.Invoke(options);
        return options;
    }

    public static IOptions<PhoenixOptions> Wrap(PhoenixOptions options) =>
        Microsoft.Extensions.Options.Options.Create(options);

    public static PhoenixPaths CreatePaths(PhoenixOptions options, string environmentName = "Development")
    {
        var paths = PhoenixPaths.Create(options, environmentName);
        paths.EnsureCreated();
        return paths;
    }

    public static PhoenixRunOptions CreateRun(Action<PhoenixRunOptions>? _ = null) =>
        new() { OperationId = OperationId.New() };

    public static FaultInjector CreateFaultInjector(PhoenixOptions options) =>
        new(Wrap(options), NullLogger<FaultInjector>.Instance);

    public static ILogger<T> Logger<T>() => NullLogger<T>.Instance;
}
