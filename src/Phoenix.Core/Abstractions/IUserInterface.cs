using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Core.Versioning;

namespace Phoenix.Core.Abstractions;

/// <summary>
/// One line of the progress narrative: "Checking your system ... ok". Disposing a stage
/// without an explicit outcome marks it complete, so an exception never leaves a dangling line.
/// </summary>
public interface IUiStage : IDisposable
{
    void Complete(string? detail = null);

    void Warn(string detail);

    void Fail(string? detail = null);
}

/// <summary>
/// Everything the person in front of the terminal sees. Deliberately narrow: no paths, no URLs,
/// no exceptions. Diagnostics belong in the log, and in diagnostic mode, in <see cref="ShowDiagnostics"/>.
/// </summary>
public interface IUserInterface
{
    /// <summary>True when the terminal can prompt (not redirected, not running under CI).</summary>
    bool IsInteractive { get; }

    bool DiagnosticMode { get; }

    void ShowWelcome(SystemInformation system);

    IUiStage BeginStage(string label);

    void ShowPrerequisites(PrerequisiteReport report);

    void ShowUpdateAvailable(SemanticVersion? current, SemanticVersion next);

    /// <summary>Runs work that reports measurable progress, showing a real progress bar.</summary>
    Task<T> RunWithProgressAsync<T>(
        string label,
        Func<IProgress<DownloadProgress>, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);

    /// <summary>Runs work of unknown duration, showing a spinner rather than a fake percentage.</summary>
    Task<T> RunWithStatusAsync<T>(
        string label,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);

    void ShowInformation(string message);

    void ShowWarning(string message);

    void ShowSuccess(string message, string? detail = null);

    /// <summary>The calm version of a failure: what happened, what Phoenix did about it, and a reference.</summary>
    void ShowFailure(PhoenixError error, string operationId, string? logDirectory, string? recoveryNote = null);

    void ShowReady(string applicationName, Uri url, bool browserOpened);

    /// <summary>Key/value diagnostics. Suppressed entirely outside diagnostic mode.</summary>
    void ShowDiagnostics(string title, IReadOnlyList<KeyValuePair<string, string>> rows);

    bool Confirm(string question, bool defaultValue);

    void ShowRebootRequired(IReadOnlyList<string> componentNames);
}
