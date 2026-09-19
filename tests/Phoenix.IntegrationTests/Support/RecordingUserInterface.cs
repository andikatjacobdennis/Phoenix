using System.Collections.Concurrent;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Core.Versioning;

namespace Phoenix.IntegrationTests.Support;

/// <summary>
/// Stands in for the Spectre.Console interface during tests: silent, and it records what the
/// user would have been told so the tests can assert on the message, not just the exit code.
/// </summary>
public sealed class RecordingUserInterface : IUserInterface
{
    public ConcurrentQueue<string> Stages { get; } = new();

    public ConcurrentQueue<string> Messages { get; } = new();

    public ConcurrentQueue<PhoenixError> Failures { get; } = new();

    public ConcurrentQueue<string> RecoveryNotes { get; } = new();

    public PrerequisiteReport? LastPrerequisiteReport { get; private set; }

    public (SemanticVersion? Current, SemanticVersion Next)? UpdateAnnouncement { get; private set; }

    public bool ReadyShown { get; private set; }

    public bool RebootRequested { get; private set; }

    public bool IsInteractive => false;

    public bool DiagnosticMode => false;

    /// <summary>Everything the run reported, so a failing assertion says why rather than just what.</summary>
    public string Describe()
    {
        var failures = Failures.Select(f => $"  {f.Code} [{f.Category}] {f.TechnicalMessage}");
        var messages = Messages.Select(m => $"  {m}");
        var stages = string.Join(" -> ", Stages);

        return $"""
            Stages: {stages}
            Failures:
            {string.Join(Environment.NewLine, failures)}
            Messages:
            {string.Join(Environment.NewLine, messages)}
            """;
    }

    public void ShowWelcome(SystemInformation system) => Messages.Enqueue("welcome");

    public IUiStage BeginStage(string label)
    {
        Stages.Enqueue(label);
        return new NoOpStage();
    }

    public void ShowPrerequisites(PrerequisiteReport report) => LastPrerequisiteReport = report;

    public void ShowUpdateAvailable(SemanticVersion? current, SemanticVersion next) =>
        UpdateAnnouncement = (current, next);

    public Task<T> RunWithProgressAsync<T>(
        string label,
        Func<IProgress<DownloadProgress>, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        Stages.Enqueue(label);
        return work(new Progress<DownloadProgress>(), cancellationToken);
    }

    public Task<T> RunWithStatusAsync<T>(
        string label,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        Stages.Enqueue(label);
        return work(cancellationToken);
    }

    public void ShowInformation(string message) => Messages.Enqueue(message);

    public void ShowWarning(string message) => Messages.Enqueue(message);

    public void ShowSuccess(string message, string? detail = null) => Messages.Enqueue(message);

    public void ShowFailure(PhoenixError error, string operationId, string? logDirectory, string? recoveryNote = null)
    {
        Failures.Enqueue(error);

        if (recoveryNote is not null)
        {
            RecoveryNotes.Enqueue(recoveryNote);
        }
    }

    public void ShowReady(string applicationName, Uri url, bool browserOpened) => ReadyShown = true;

    public void ShowDiagnostics(string title, IReadOnlyList<KeyValuePair<string, string>> rows)
    {
        // Diagnostics are suppressed outside diagnostic mode.
    }

    public bool Confirm(string question, bool defaultValue) => defaultValue;

    public void ShowRebootRequired(IReadOnlyList<string> componentNames) => RebootRequested = true;

    private sealed class NoOpStage : IUiStage
    {
        public void Complete(string? detail = null)
        {
        }

        public void Warn(string detail)
        {
        }

        public void Fail(string? detail = null)
        {
        }

        public void Dispose()
        {
        }
    }
}
