namespace Phoenix.Core.Errors;

/// <summary>Broad classification of a failure, used for logging, retry and recovery decisions.</summary>
public enum ErrorCategory
{
    Unknown = 0,
    Configuration,
    Network,
    GitHub,
    Download,
    Security,
    Verification,
    Prerequisite,
    Installation,
    Startup,
    HealthCheck,
    Recovery,
    Permission,
    DiskSpace,
    Cancellation,
}
