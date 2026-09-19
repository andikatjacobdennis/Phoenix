using Phoenix.Core.Models;

namespace Phoenix.Core.Configuration;

/// <summary>
/// Everything a company needs to change in order to adopt Phoenix. Phoenix internals are
/// never edited for branding, repository or path differences: it all lives here.
/// </summary>
public sealed class PhoenixOptions
{
    public const string SectionName = "Phoenix";

    public CompanyOptions Company { get; set; } = new();

    public ProductOptions Product { get; set; } = new();

    public BrandingOptions Branding { get; set; } = new();

    public GitHubOptions GitHub { get; set; } = new();

    public ApplicationOptions Application { get; set; } = new();

    public UpdateOptions Updates { get; set; } = new();

    public PrerequisitesOptions Prerequisites { get; set; } = new();

    public DirectoryOptions Directories { get; set; } = new();

    public HealthCheckOptions HealthCheck { get; set; } = new();

    public RecoveryOptions Recovery { get; set; } = new();

    public NetworkOptions Network { get; set; } = new();

    public SecurityOptions Security { get; set; } = new();

    public BrowserOptions Browser { get; set; } = new();

    public TestingOptions Testing { get; set; } = new();
}

public sealed class CompanyOptions
{
    public string Name { get; set; } = "Example Company";

    public string? SupportEmail { get; set; }

    public string? SupportUrl { get; set; }

    /// <summary>Shown at the bottom of a failure screen, e.g. "Contact IT on x1234".</summary>
    public string? SupportText { get; set; }
}

public sealed class ProductOptions
{
    public string Name { get; set; } = "Example Application";

    /// <summary>Filesystem-safe short name used for directories. Derived from Name when empty.</summary>
    public string? Slug { get; set; }
}

public sealed class BrandingOptions
{
    public string? ConsoleTitle { get; set; }

    public string? WelcomeText { get; set; }

    /// <summary>Spectre.Console colour name or hex used for the primary accent.</summary>
    public string AccentColor { get; set; } = "deepskyblue1";

    /// <summary>When false, Phoenix does not greet the user by name.</summary>
    public bool GreetUserByName { get; set; } = true;
}

public sealed class GitHubOptions
{
    public string Owner { get; set; } = string.Empty;

    public string Repository { get; set; } = string.Empty;

    /// <summary>Overridable for GitHub Enterprise and for integration tests.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>
    /// Name of the environment variable holding a token for private repositories.
    /// The token value itself is never stored in configuration and never logged.
    /// </summary>
    public string TokenEnvironmentVariable { get; set; } = "PHOENIX_GITHUB_TOKEN";

    public bool IncludeDrafts { get; set; }

    /// <summary>How many releases to inspect when looking for candidates.</summary>
    public int MaxReleasesToInspect { get; set; } = 30;
}

public sealed class ApplicationOptions
{
    /// <summary>Must match <c>applicationId</c> in the release manifest, or the release is rejected.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Where the application is expected to serve, used for the browser launch.</summary>
    public string Url { get; set; } = "http://localhost:5080";

    /// <summary>Extra arguments appended to the manifest arguments.</summary>
    public string[] AdditionalArguments { get; set; } = [];

    /// <summary>Extra environment variables applied when starting the application.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];

    public int ShutdownTimeoutSeconds { get; set; } = 20;

    /// <summary>How long to wait before deciding an immediate exit counts as a startup crash.</summary>
    public int StartupGraceSeconds { get; set; } = 2;

    /// <summary>When true, an already-running instance is reused instead of being restarted.</summary>
    public bool ReuseRunningInstance { get; set; } = true;
}

public enum UpdatePolicy
{
    /// <summary>Install newer releases without asking.</summary>
    Automatic = 0,

    /// <summary>Report that an update exists, but launch the installed version.</summary>
    CheckOnly,

    /// <summary>Never contact the release source once something is installed.</summary>
    Never,

    /// <summary>Install exactly <see cref="UpdateOptions.PinnedVersion"/>.</summary>
    Pinned,
}

public sealed class UpdateOptions
{
    public ReleaseChannel Channel { get; set; } = ReleaseChannel.Production;

    public UpdatePolicy Policy { get; set; } = UpdatePolicy.Automatic;

    public string? PinnedVersion { get; set; }

    /// <summary>How many older releases may be attempted when the newest one fails.</summary>
    public int RemoteFallbackCount { get; set; } = 2;

    public bool AllowDowngrade { get; set; }

    /// <summary>Ask before installing an update instead of proceeding automatically.</summary>
    public bool ConfirmBeforeUpdate { get; set; }
}

public sealed class DirectoryOptions
{
    /// <summary>
    /// Root of everything Phoenix owns. Environment variables are expanded, and
    /// <c>{ProductSlug}</c> is replaced with the product slug.
    /// </summary>
    public string Root { get; set; } = @"%LOCALAPPDATA%\Phoenix\{ProductSlug}";

    public string? Versions { get; set; }

    public string? Staging { get; set; }

    public string? Backup { get; set; }

    public string? Cache { get; set; }

    public string? Logs { get; set; }

    public string? State { get; set; }
}

public sealed class HealthCheckOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Relative path appended to the application URL when <see cref="Url"/> is empty.</summary>
    public string Path { get; set; } = "/health";

    /// <summary>Absolute health-check URL. Overrides <see cref="Path"/>.</summary>
    public string? Url { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 5;

    public int RetryIntervalMilliseconds { get; set; } = 1000;

    public int MaxAttempts { get; set; } = 40;

    public int OverallTimeoutSeconds { get; set; } = 90;

    public int[] ExpectedStatusCodes { get; set; } = [200];
}

public sealed class RecoveryOptions
{
    public bool EnableAutomaticRollback { get; set; } = true;

    /// <summary>How many previous versions are retained on disk for recovery.</summary>
    public int KeepVersions { get; set; } = 3;

    /// <summary>How many backup copies of known-good installations are retained.</summary>
    public int BackupRetention { get; set; } = 2;

    /// <summary>A version is only marked known-good after a successful health check.</summary>
    public bool RequireHealthCheckForKnownGood { get; set; } = true;
}

public sealed class NetworkOptions
{
    public int RequestTimeoutSeconds { get; set; } = 30;

    public int DownloadTimeoutMinutes { get; set; } = 30;

    public int MaxRetries { get; set; } = 3;

    public int RetryBaseDelayMilliseconds { get; set; } = 500;

    /// <summary>Appended to the Phoenix user agent, e.g. "ExampleCompany".</summary>
    public string? UserAgentSuffix { get; set; }
}

public sealed class SecurityOptions
{
    public bool RequireHttps { get; set; } = true;

    public bool RequireChecksum { get; set; } = true;

    /// <summary>Verify the Authenticode signature of downloaded executables and installers.</summary>
    public bool RequireSignature { get; set; }

    /// <summary>When signature verification is on, only these thumbprints are accepted (empty = any valid chain).</summary>
    public string[] AllowedCertificateThumbprints { get; set; } = [];

    /// <summary>Hosts Phoenix is willing to download from. Empty means "any HTTPS host".</summary>
    public string[] AllowedDownloadHosts { get; set; } =
    [
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "api.github.com",
    ];

    public long MaxPackageSizeBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public long MaxExtractedSizeBytes { get; set; } = 8L * 1024 * 1024 * 1024;

    public int MaxArchiveEntries { get; set; } = 200_000;

    /// <summary>Extra free space required beyond the computed need, as a safety margin.</summary>
    public long DiskSpaceMarginBytes { get; set; } = 512L * 1024 * 1024;
}

public sealed class BrowserOptions
{
    public bool LaunchOnSuccess { get; set; } = true;

    /// <summary>Overrides the application URL for the browser launch only.</summary>
    public string? Url { get; set; }
}

/// <summary>Faults Phoenix can inject into its own pipeline so recovery paths can be exercised.</summary>
public enum SimulatedFault
{
    None = 0,
    DownloadFailure,
    InterruptedDownload,
    ChecksumMismatch,
    CorruptArchive,
    StartupFailure,
    HealthCheckFailure,
    ActivationFailure,
}

public sealed class TestingOptions
{
    /// <summary>
    /// Fault injected into the next install attempt. Rejected by validation outside Development,
    /// so a production configuration can never sabotage itself.
    /// </summary>
    public SimulatedFault SimulateFault { get; set; } = SimulatedFault.None;

    /// <summary>
    /// When true, prerequisite installers are logged and reported as successful but never executed.
    /// Automated tests must never change machine-wide software.
    /// </summary>
    public bool SimulatePrerequisiteInstalls { get; set; }

    /// <summary>When false, Phoenix will not open a browser even if configured to.</summary>
    public bool AllowBrowserLaunch { get; set; } = true;
}
