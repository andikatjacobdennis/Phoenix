using Phoenix.Core.Diagnostics;
using Phoenix.Core.Orchestration;

namespace Phoenix;

/// <summary>Parsed command line, or a reason to stop before doing anything.</summary>
internal sealed record CommandLine
{
    public PhoenixRunOptions Run { get; init; } = new() { OperationId = OperationId.New() };

    public string? Environment { get; init; }

    public string? ConfigurationFile { get; init; }

    public bool ShowHelp { get; init; }

    public bool ShowVersion { get; init; }

    public string? Error { get; init; }

    public static CommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var diagnostics = false;
        var checkOnly = false;
        var forceReinstall = false;
        var rollback = false;
        var noBrowser = false;
        var noLaunch = false;
        var assumeYes = false;
        var help = false;
        var version = false;
        string? environment = null;
        string? configurationFile = null;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            switch (argument.ToLowerInvariant())
            {
                case "-d":
                case "--diagnostics":
                case "--verbose":
                    diagnostics = true;
                    break;

                case "--check-only":
                    checkOnly = true;
                    break;

                case "--force-reinstall":
                    forceReinstall = true;
                    break;

                case "--rollback":
                    rollback = true;
                    break;

                case "--no-browser":
                    noBrowser = true;
                    break;

                case "--no-launch":
                    noLaunch = true;
                    break;

                case "-y":
                case "--yes":
                    assumeYes = true;
                    break;

                case "-e":
                case "--environment":
                    if (i + 1 >= args.Length)
                    {
                        return new CommandLine { Error = "--environment needs a value, for example --environment QA." };
                    }

                    environment = args[++i];
                    break;

                case "-c":
                case "--config":
                    if (i + 1 >= args.Length)
                    {
                        return new CommandLine { Error = "--config needs a path to a JSON configuration file." };
                    }

                    configurationFile = args[++i];
                    break;

                case "-h":
                case "-?":
                case "--help":
                    help = true;
                    break;

                case "--version":
                    version = true;
                    break;

                default:
                    return new CommandLine { Error = $"'{argument}' is not a known option." };
            }
        }

        return new CommandLine
        {
            Environment = environment,
            ConfigurationFile = configurationFile,
            ShowHelp = help,
            ShowVersion = version,
            Run = new PhoenixRunOptions
            {
                Diagnostics = diagnostics,
                CheckOnly = checkOnly,
                ForceReinstall = forceReinstall,
                Rollback = rollback,
                NoBrowser = noBrowser,
                NoLaunch = noLaunch,
                AssumeYes = assumeYes,
                OperationId = OperationId.New(),
            },
        };
    }

    public static string HelpText =>
        """
        Phoenix - installs, updates and launches your application.

        Usage:
          phoenix [options]

        Options:
          -d, --diagnostics       Show paths, versions and release details.
              --check-only        Report whether an update exists, then launch what is installed.
              --force-reinstall   Install the selected release even if it is already present.
              --rollback          Restore and launch the last known-good version.
              --no-browser        Do not open a browser after a successful start.
              --no-launch         Install only; do not start the application.
          -y, --yes               Answer yes to confirmations.
          -e, --environment NAME  Development, QA or Production (default: Production).
          -c, --config PATH       Load an additional JSON configuration file.
              --version           Print the Phoenix version and exit.
          -h, --help              Show this help.

        Exit codes:
          0  success                4  a prerequisite could not be installed
          1  unexpected failure     5  installation failed (previous version left intact)
          2  cancelled              6  installation and recovery both failed
          3  configuration error    7  another Phoenix instance is running
                                    8  restart required before Phoenix can continue
        """;
}
