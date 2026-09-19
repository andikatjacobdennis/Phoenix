namespace Phoenix.Core.Orchestration;

/// <summary>Per-run switches that come from the command line rather than from configuration.</summary>
public sealed class PhoenixRunOptions
{
    /// <summary>Show paths, versions, release selection and health attempts. Off by default.</summary>
    public bool Diagnostics { get; init; }

    /// <summary>Report what would happen, then launch the installed version without updating.</summary>
    public bool CheckOnly { get; init; }

    /// <summary>Install the selected release even when the same version is already present.</summary>
    public bool ForceReinstall { get; init; }

    /// <summary>Restore the known-good installation and launch it, without contacting the release source.</summary>
    public bool Rollback { get; init; }

    /// <summary>Do not open a browser, whatever configuration says.</summary>
    public bool NoBrowser { get; init; }

    /// <summary>Do not start the application; prepare the installation only.</summary>
    public bool NoLaunch { get; init; }

    /// <summary>Answer every confirmation with yes. Implied when the console is not interactive.</summary>
    public bool AssumeYes { get; init; }

    /// <summary>Unique identifier for this run, shown to users as "Reference: PX-XXXXXX".</summary>
    public required string OperationId { get; init; }
}
