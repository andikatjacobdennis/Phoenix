using Microsoft.Extensions.Configuration;
using Phoenix.Core.Configuration;
using Phoenix.Core.Diagnostics;
using Phoenix.Core.Orchestration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Phoenix;

/// <summary>
/// Diagnostics live in the log file, not on the screen.
///
/// Every entry carries the operation id that the user sees as "Reference: PX-XXXXXX", so a
/// support request maps to exactly one run. The console sink stays off unless diagnostic mode
/// is requested, because log lines interleaved with progress bars help nobody.
/// </summary>
internal static class SerilogSetup
{
    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{OperationId}] {Message:lj}" +
        "{NewLine}{Exception}";

    private const string ConsoleTemplate =
        "[grey]{Timestamp:HH:mm:ss}[/] [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static Logger CreateLogger(
        IConfiguration configuration,
        PhoenixPaths paths,
        PhoenixOptions options,
        PhoenixRunOptions run,
        string environmentName)
    {
        var configured = new LoggerConfiguration()
            .MinimumLevel.Is(run.Diagnostics ? LogEventLevel.Debug : LogEventLevel.Information)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty(OperationId.LogPropertyName, run.OperationId)
            .Enrich.WithProperty("PhoenixVersion", PhoenixVersionInfo.DisplayVersion)
            .Enrich.WithProperty("Environment", environmentName)
            .Enrich.WithProperty("Product", options.Product.Name)
            .Enrich.WithProperty("Channel", options.Updates.Channel.ToString())
            .WriteTo.File(
                Path.Combine(paths.Logs, "phoenix-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 32L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: FileTemplate);

        if (run.Diagnostics)
        {
            configured = configured.WriteTo.Console(outputTemplate: ConsoleTemplate);
        }

        // A "Serilog" section in configuration can add sinks or raise levels without a rebuild.
        if (configuration.GetSection("Serilog").Exists())
        {
            configured = configured.ReadFrom.Configuration(configuration);
        }

        return configured.CreateLogger();
    }
}
