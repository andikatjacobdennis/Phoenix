namespace Phoenix.Core.Errors;

/// <summary>
/// Process exit codes reported by Phoenix. Deliberately small and stable:
/// support staff should be able to memorise them.
/// </summary>
public enum ExitCode
{
    /// <summary>Everything completed and the application is running.</summary>
    Success = 0,

    /// <summary>An unexpected failure that does not map to a more specific code.</summary>
    UnknownFailure = 1,

    /// <summary>The user cancelled (Ctrl+C or an explicit exit choice).</summary>
    Cancelled = 2,

    /// <summary>Configuration is missing or invalid; Phoenix never started work.</summary>
    ConfigurationError = 3,

    /// <summary>A mandatory prerequisite could not be detected or installed.</summary>
    PrerequisiteFailure = 4,

    /// <summary>Download, verification or installation failed, but the machine is in a safe state.</summary>
    InstallationFailure = 5,

    /// <summary>Installation failed and recovery also failed: manual intervention required.</summary>
    RecoveryFailure = 6,

    /// <summary>Another Phoenix instance already owns this installation.</summary>
    AlreadyRunning = 7,

    /// <summary>Work is paused until the machine is restarted.</summary>
    RebootRequired = 8,
}
