namespace Phoenix.Core.Configuration;

public sealed class PrerequisitesOptions
{
    /// <summary>When false, the whole prerequisite phase is skipped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Install missing prerequisites, rather than only reporting them.</summary>
    public bool InstallMissing { get; set; } = true;

    public List<PrerequisiteDefinition> Required { get; set; } = [];
}

/// <summary>How a prerequisite is found on the machine.</summary>
public enum DetectionStrategy
{
    /// <summary>Enumerate installed .NET runtimes (shared framework folders and <c>dotnet --list-runtimes</c>).</summary>
    DotNetRuntime = 0,

    /// <summary>Run an executable with a version argument and parse the output.</summary>
    ExecutableVersion,

    /// <summary>Read the file version of a file on disk.</summary>
    FileVersion,

    /// <summary>Read a Windows registry value.</summary>
    Registry,

    /// <summary>Run an arbitrary command and parse its output.</summary>
    Command,

    /// <summary>Always reports missing. For demonstrating and testing the install path only.</summary>
    AssumeMissing,

    /// <summary>Always reports satisfied. For demonstrating and testing only.</summary>
    AssumePresent,
}

public sealed class PrerequisiteDefinition
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>A missing mandatory prerequisite stops the run; an optional one only warns.</summary>
    public bool Mandatory { get; set; } = true;

    public string? RequiredVersion { get; set; }

    public string? MinimumVersion { get; set; }

    public string? MaximumVersion { get; set; }

    /// <summary>Interval notation, e.g. <c>[9.0,11.0)</c>. Takes precedence over the fields above.</summary>
    public string? VersionRange { get; set; }

    /// <summary>x64, x86, arm64 or any.</summary>
    public string Architecture { get; set; } = "any";

    /// <summary>Environments this prerequisite applies to. Empty means all environments.</summary>
    public string[] ApplicableEnvironments { get; set; } = [];

    public DetectionOptions Detection { get; set; } = new();

    public PrerequisiteInstallerOptions? Installer { get; set; }

    public bool RequiresAdministrator { get; set; }

    /// <summary>The installer is known to require a restart even when it reports success.</summary>
    public bool RequiresRestart { get; set; }

    public int TimeoutSeconds { get; set; } = 900;
}

public sealed class DetectionOptions
{
    public DetectionStrategy Strategy { get; set; } = DetectionStrategy.DotNetRuntime;

    /// <summary>Shared framework name for <see cref="DetectionStrategy.DotNetRuntime"/>, e.g. <c>Microsoft.AspNetCore.App</c>.</summary>
    public string? RuntimeName { get; set; }

    /// <summary>Executable or file path. Environment variables are expanded; bare names are resolved on PATH.</summary>
    public string? Path { get; set; }

    /// <summary>Arguments passed when probing the executable, e.g. <c>--version</c>.</summary>
    public string? Arguments { get; set; }

    /// <summary>Regular expression with a <c>version</c> capture group applied to the command output.</summary>
    public string? VersionPattern { get; set; }

    /// <summary>Registry hive: <c>HKLM</c> or <c>HKCU</c>.</summary>
    public string? RegistryHive { get; set; }

    public string? RegistryKey { get; set; }

    public string? RegistryValue { get; set; }

    /// <summary>Read the 32-bit registry view instead of the native one.</summary>
    public bool RegistryWow6432 { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class PrerequisiteInstallerOptions
{
    /// <summary>HTTPS URL of the installer package.</summary>
    public string? Url { get; set; }

    /// <summary>Already-present installer on disk. Used instead of <see cref="Url"/> when set.</summary>
    public string? LocalPath { get; set; }

    /// <summary>Cache file name. Derived from the URL when empty.</summary>
    public string? FileName { get; set; }

    /// <summary>Hex SHA-256 of the installer. Required whenever <see cref="Url"/> is used.</summary>
    public string? Sha256 { get; set; }

    public string SilentArguments { get; set; } = string.Empty;

    public int[] AcceptedExitCodes { get; set; } = [0];

    /// <summary>Exit codes meaning "installed, but restart required". 3010 and 1641 are the Windows conventions.</summary>
    public int[] RebootExitCodes { get; set; } = [3010, 1641];

    public long? SizeBytes { get; set; }
}
