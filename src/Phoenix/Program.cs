using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Configuration;
using Phoenix.Core.Diagnostics;
using Phoenix.Core.Errors;
using Phoenix.Core.Orchestration;
using Phoenix.Infrastructure.DependencyInjection;
using Serilog;
using Spectre.Console;

namespace Phoenix;

internal static class Program
{
    private const string EnvironmentVariableName = "PHOENIX_ENVIRONMENT";

    private static async Task<int> Main(string[] args)
    {
        var commandLine = CommandLine.Parse(args);

        if (commandLine.Error is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(commandLine.Error)}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine(CommandLine.HelpText);
            return (int)ExitCode.ConfigurationError;
        }

        if (commandLine.ShowHelp)
        {
            AnsiConsole.WriteLine(CommandLine.HelpText);
            return (int)ExitCode.Success;
        }

        if (commandLine.ShowVersion)
        {
            AnsiConsole.WriteLine(PhoenixVersionInfo.DisplayVersion);
            return (int)ExitCode.Success;
        }

        var environmentName = ResolveEnvironmentName(commandLine);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ApplicationName = "Phoenix",
            EnvironmentName = environmentName,
            ContentRootPath = AppContext.BaseDirectory,
        });

        if (!string.IsNullOrWhiteSpace(commandLine.ConfigurationFile))
        {
            builder.Configuration.AddJsonFile(commandLine.ConfigurationFile, optional: false, reloadOnChange: false);
        }

        // Environment variables such as PHOENIX_Phoenix__Updates__Channel=QA override files,
        // which is how a deployment tool customises a machine without editing JSON.
        builder.Configuration.AddEnvironmentVariables("PHOENIX_");

        // Validate before anything else so a typo produces one clear sentence rather than a
        // failure halfway through an update.
        var options = new PhoenixOptions();
        builder.Configuration.GetSection(PhoenixOptions.SectionName).Bind(options);

        var validation = new PhoenixOptionsValidator(environmentName).Validate(null, options);
        if (validation.Failed)
        {
            ReportConfigurationProblem(validation.Failures?.ToList() ?? [], environmentName);
            return (int)ExitCode.ConfigurationError;
        }

        PhoenixPaths paths;
        try
        {
            paths = PhoenixPaths.Create(options, environmentName);
            paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ReportConfigurationProblem(
                [$"The Phoenix directories under '{options.Directories.Root}' could not be created: {ex.Message}"],
                environmentName);
            return (int)ExitCode.ConfigurationError;
        }

        Log.Logger = SerilogSetup.CreateLogger(
            builder.Configuration,
            paths,
            options,
            commandLine.Run,
            environmentName);

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(Log.Logger, dispose: false);
        builder.Services.AddPhoenix(builder.Configuration, environmentName, commandLine.Run);

        SetConsoleTitle(options);

        using var cancellation = new CancellationTokenSource();
        var cancelled = 0;

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // The first Ctrl+C asks Phoenix to stop at the next safe point. A second one is
            // left to the runtime, so a user is never trapped.
            if (Interlocked.Exchange(ref cancelled, 1) == 0)
            {
                eventArgs.Cancel = true;
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]Stopping safely...[/]");
                cancellation.Cancel();
            }
        };

        try
        {
            using var host = builder.Build();
            var orchestrator = host.Services.GetRequiredService<PhoenixOrchestrator>();
            var exitCode = await orchestrator.RunAsync(cancellation.Token).ConfigureAwait(false);

            Log.Information("Phoenix finished with exit code {ExitCode}.", (int)exitCode);
            return (int)exitCode;
        }
        catch (OptionsValidationException ex)
        {
            ReportConfigurationProblem(ex.Failures.ToList(), environmentName);
            return (int)ExitCode.ConfigurationError;
        }
        catch (OperationCanceledException)
        {
            return (int)ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Phoenix stopped unexpectedly.");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red]Something went wrong and Phoenix had to stop.[/]");
            AnsiConsole.MarkupLine($"[grey]Reference: {Markup.Escape(commandLine.Run.OperationId)}[/]");
            return (int)ExitCode.UnknownFailure;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static string ResolveEnvironmentName(CommandLine commandLine)
    {
        if (!string.IsNullOrWhiteSpace(commandLine.Environment))
        {
            return commandLine.Environment;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var dotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        return string.IsNullOrWhiteSpace(dotnetEnvironment) ? "Production" : dotnetEnvironment;
    }

    private static void SetConsoleTitle(PhoenixOptions options)
    {
        try
        {
            var title = string.IsNullOrWhiteSpace(options.Branding.ConsoleTitle)
                ? $"{options.Company.Name} {options.Product.Name} Setup"
                : options.Branding.ConsoleTitle;

            Console.Title = title;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // Redirected output has no title to set.
        }
    }

    private static void ReportConfigurationProblem(IReadOnlyCollection<string> failures, string environmentName)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[red]Phoenix is not set up correctly on this computer.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[grey]Environment: {Markup.Escape(environmentName)}[/]");
        AnsiConsole.WriteLine();

        foreach (var failure in failures)
        {
            AnsiConsole.MarkupLine($"  [grey]-[/] {Markup.Escape(failure)}");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Fix appsettings.json (or the matching environment file) and run Phoenix again.[/]");
        AnsiConsole.WriteLine();
    }
}
