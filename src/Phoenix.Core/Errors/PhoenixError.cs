using System.Collections.Immutable;

namespace Phoenix.Core.Errors;

/// <summary>
/// A failure with two faces: a calm sentence for the person in front of the terminal,
/// and the full technical story for the log file.
/// </summary>
public sealed record PhoenixError
{
    public required ErrorCategory Category { get; init; }

    /// <summary>Stable machine-readable code, for example <c>PX-NET-TIMEOUT</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Safe to show to a normal user. Never contains paths, URLs or stack traces.</summary>
    public required string UserMessage { get; init; }

    /// <summary>Written to the log. May contain paths, URLs, status codes and exit codes.</summary>
    public required string TechnicalMessage { get; init; }

    public Exception? Exception { get; init; }

    /// <summary>True when repeating the identical operation could plausibly succeed.</summary>
    public bool IsRetryable { get; init; }

    /// <summary>True when trying a different (older) remote release is a sensible next step.</summary>
    public bool AllowsRemoteFallback { get; init; }

    /// <summary>True when the local installation may have been disturbed and must be restored.</summary>
    public bool RequiresRollback { get; init; }

    public ExitCode ExitCode { get; init; } = ExitCode.UnknownFailure;

    public ImmutableDictionary<string, object?> Context { get; init; } =
        ImmutableDictionary<string, object?>.Empty;

    public PhoenixError With(string key, object? value) =>
        this with { Context = Context.SetItem(key, value) };

    public override string ToString() => $"{Code} [{Category}] {TechnicalMessage}";

    // ---- Factories -------------------------------------------------------
    // Retry / fallback / rollback policy is decided here, in one place, rather
    // than being re-invented at every call site.

    public static PhoenixError Configuration(string code, string technical, Exception? ex = null) => new()
    {
        Category = ErrorCategory.Configuration,
        Code = code,
        UserMessage = "Phoenix is not set up correctly on this computer.",
        TechnicalMessage = technical,
        Exception = ex,
        ExitCode = ExitCode.ConfigurationError,
    };

    public static PhoenixError Network(string code, string technical, Exception? ex = null, bool retryable = true) => new()
    {
        Category = ErrorCategory.Network,
        Code = code,
        UserMessage = "We could not reach the update service.",
        TechnicalMessage = technical,
        Exception = ex,
        IsRetryable = retryable,
        ExitCode = ExitCode.InstallationFailure,
    };

    public static PhoenixError GitHub(string code, string technical, Exception? ex = null, bool retryable = false) => new()
    {
        Category = ErrorCategory.GitHub,
        Code = code,
        UserMessage = "We could not check for updates right now.",
        TechnicalMessage = technical,
        Exception = ex,
        IsRetryable = retryable,
        ExitCode = ExitCode.InstallationFailure,
    };

    public static PhoenixError Download(string code, string technical, Exception? ex = null, bool retryable = true) => new()
    {
        Category = ErrorCategory.Download,
        Code = code,
        UserMessage = "We could not download the update.",
        TechnicalMessage = technical,
        Exception = ex,
        IsRetryable = retryable,
        AllowsRemoteFallback = true,
        ExitCode = ExitCode.InstallationFailure,
    };

    /// <summary>A package failed an integrity or authenticity check. Never retried, never trusted.</summary>
    public static PhoenixError Verification(string code, string technical, Exception? ex = null) => new()
    {
        Category = ErrorCategory.Verification,
        Code = code,
        UserMessage = "The downloaded update did not pass our safety checks.",
        TechnicalMessage = technical,
        Exception = ex,
        IsRetryable = false,
        AllowsRemoteFallback = true,
        ExitCode = ExitCode.InstallationFailure,
    };

    public static PhoenixError Security(string code, string technical, Exception? ex = null) => new()
    {
        Category = ErrorCategory.Security,
        Code = code,
        UserMessage = "The update was blocked because it did not look safe.",
        TechnicalMessage = technical,
        Exception = ex,
        IsRetryable = false,
        AllowsRemoteFallback = true,
        ExitCode = ExitCode.InstallationFailure,
    };

    public static PhoenixError Prerequisite(string code, string technical, Exception? ex = null) => new()
    {
        Category = ErrorCategory.Prerequisite,
        Code = code,
        UserMessage = "A required component could not be installed.",
        TechnicalMessage = technical,
        Exception = ex,
        ExitCode = ExitCode.PrerequisiteFailure,
    };

    public static PhoenixError Installation(string code, string technical, Exception? ex = null, bool requiresRollback = true) => new()
    {
        Category = ErrorCategory.Installation,
        Code = code,
        UserMessage = "We could not finish installing the application.",
        TechnicalMessage = technical,
        Exception = ex,
        AllowsRemoteFallback = true,
        RequiresRollback = requiresRollback,
        ExitCode = ExitCode.InstallationFailure,
    };

    public static PhoenixError Startup(string code, string technical, Exception? ex = null) => new()
    {
        Category = ErrorCategory.Startup,
        Code = code,
        UserMessage = "The application could not be started.",
        TechnicalMessage = technical,
        Exception = ex,
        AllowsRemoteFallback = true,
        RequiresRollback = true,
        ExitCode = ExitCode.InstallationFailure,
    };

    public static PhoenixError HealthCheck(string code, string technical, Exception? ex = null) => new()
    {
        Category = ErrorCategory.HealthCheck,
        Code = code,
        UserMessage = "The application started but never became ready.",
        TechnicalMessage = technical,
        Exception = ex,
        AllowsRemoteFallback = true,
        RequiresRollback = true,
        ExitCode = ExitCode.InstallationFailure,
    };

    public static PhoenixError Recovery(string code, string technical, Exception? ex = null) => new()
    {
        Category = ErrorCategory.Recovery,
        Code = code,
        UserMessage = "We could not restore your previous working version.",
        TechnicalMessage = technical,
        Exception = ex,
        ExitCode = ExitCode.RecoveryFailure,
    };

    public static PhoenixError Permission(string code, string technical, Exception? ex = null) => new()
    {
        Category = ErrorCategory.Permission,
        Code = code,
        UserMessage = "Phoenix does not have permission to complete this step.",
        TechnicalMessage = technical,
        Exception = ex,
        ExitCode = ExitCode.InstallationFailure,
    };

    public static PhoenixError DiskSpace(string code, string technical) => new()
    {
        Category = ErrorCategory.DiskSpace,
        Code = code,
        UserMessage = "There is not enough free disk space to install the application.",
        TechnicalMessage = technical,
        ExitCode = ExitCode.InstallationFailure,
    };

    public static PhoenixError Cancelled(string technical = "Operation cancelled by the user.") => new()
    {
        Category = ErrorCategory.Cancellation,
        Code = "PX-CANCELLED",
        UserMessage = "Cancelled.",
        TechnicalMessage = technical,
        ExitCode = ExitCode.Cancelled,
    };

    public static PhoenixError Unexpected(string code, string technical, Exception? ex = null) => new()
    {
        Category = ErrorCategory.Unknown,
        Code = code,
        UserMessage = "Something went wrong.",
        TechnicalMessage = technical,
        Exception = ex,
        ExitCode = ExitCode.UnknownFailure,
    };
}
