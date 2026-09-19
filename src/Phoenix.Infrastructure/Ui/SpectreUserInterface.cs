using System.Globalization;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Core.Orchestration;
using Phoenix.Core.Versioning;
using Spectre.Console;

namespace Phoenix.Infrastructure.Ui;

/// <summary>
/// The face of Phoenix.
///
/// Two ideas run through this class. First, the user is told what is happening and nothing
/// else: no paths, no URLs, no exception text - those go to the log, and to diagnostic mode.
/// Second, the layout adapts to the terminal, because Phoenix runs in a maximised Windows
/// Terminal and in an 60-column Command Prompt window, and both should look deliberate.
/// </summary>
public sealed class SpectreUserInterface : IUserInterface
{
    private const int NarrowWidth = 60;

    private readonly IAnsiConsole _console;
    private readonly PhoenixOptions _options;
    private readonly ConsoleTheme _theme;
    private readonly PhoenixRunOptions _run;

    public SpectreUserInterface(
        IAnsiConsole console,
        IOptions<PhoenixOptions> options,
        PhoenixRunOptions run)
    {
        ArgumentNullException.ThrowIfNull(options);

        _console = console;
        _options = options.Value;
        _run = run;
        _theme = new ConsoleTheme(_options.Branding.AccentColor);
    }

    public bool IsInteractive => _console.Profile.Capabilities.Interactive && !Console.IsInputRedirected;

    public bool DiagnosticMode => _run.Diagnostics;

    private int Width => Math.Max(20, _console.Profile.Width);

    private bool IsNarrow => Width < NarrowWidth;

    private string Tick => _console.Profile.Capabilities.Unicode ? "✓" : "ok";

    private string Cross => _console.Profile.Capabilities.Unicode ? "✗" : "x";

    public void ShowWelcome(SystemInformation system)
    {
        ArgumentNullException.ThrowIfNull(system);

        var title = string.IsNullOrWhiteSpace(_options.Branding.ConsoleTitle)
            ? "Phoenix"
            : _options.Branding.ConsoleTitle;

        var subtitle = string.IsNullOrWhiteSpace(_options.Branding.WelcomeText)
            ? $"{_options.Company.Name} {_options.Product.Name}"
            : _options.Branding.WelcomeText;

        _console.WriteLine();

        if (IsNarrow)
        {
            // A panel would waste four columns of a narrow window on borders.
            _console.MarkupLine($"[{_theme.AccentMarkup} bold]{Markup.Escape(title)}[/]");
            _console.MarkupLine($"[{ConsoleTheme.Muted.ToMarkup()}]{Markup.Escape(subtitle)}[/]");
        }
        else
        {
            var content = new Markup(
                $"[{_theme.AccentMarkup} bold]{Markup.Escape(title)}[/]\n" +
                $"[{ConsoleTheme.Muted.ToMarkup()}]{Markup.Escape(subtitle)}[/]");

            _console.Write(new Panel(content)
            {
                // A console stuck on a legacy code page renders box-drawing characters as
                // noise, so fall back to ASCII rather than producing a mess.
                Border = _console.Profile.Capabilities.Unicode ? BoxBorder.Rounded : BoxBorder.Ascii,
                BorderStyle = new Style(_theme.Accent),
                Padding = new Padding(2, 0, 2, 0),
            });
        }

        _console.WriteLine();

        if (_options.Branding.GreetUserByName)
        {
            _console.MarkupLine($"Welcome, [bold]{Markup.Escape(system.UserDisplayName)}[/].");
            _console.WriteLine();
        }
    }

    public IUiStage BeginStage(string label) => new Stage(this, label);

    public void ShowPrerequisites(PrerequisiteReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var interesting = report.Items
            .Where(i => i.State != PrerequisiteState.Skipped || i.IsMandatory)
            .ToList();

        if (interesting.Count == 0)
        {
            return;
        }

        _console.WriteLine();
        _console.MarkupLine($"[{ConsoleTheme.Muted.ToMarkup()}]Required components[/]");

        foreach (var item in interesting)
        {
            var (symbol, color, text) = Describe(item);
            WritePaddedLine(
                Markup.Escape(item.DisplayName),
                $"[{color.ToMarkup()}]{symbol} {Markup.Escape(text)}[/]",
                plainRightLength: symbol.Length + 1 + text.Length);
        }

        _console.WriteLine();
    }

    private (string Symbol, Color Color, string Text) Describe(PrerequisiteStatus item) => item.State switch
    {
        PrerequisiteState.Satisfied => (Tick, ConsoleTheme.Success, DescribeVersion(item, "Ready")),
        PrerequisiteState.Installed => (Tick, ConsoleTheme.Success, DescribeVersion(item, "Installed")),
        PrerequisiteState.RebootRequired => ("!", ConsoleTheme.Warning, "Restart needed"),
        PrerequisiteState.Missing => ("!", ConsoleTheme.Warning, "Missing"),
        PrerequisiteState.Failed => (Cross, ConsoleTheme.Failure, "Not available"),
        _ => ("-", ConsoleTheme.Muted, "Skipped"),
    };

    private string DescribeVersion(PrerequisiteStatus item, string fallback) =>
        !IsNarrow && item.DetectedVersion is not null && DiagnosticMode
            ? $"{fallback} {item.DetectedVersion}"
            : fallback;

    public void ShowUpdateAvailable(SemanticVersion? current, SemanticVersion next)
    {
        ArgumentNullException.ThrowIfNull(next);

        _console.WriteLine();

        if (current is null)
        {
            _console.MarkupLine($"Preparing [bold]{Markup.Escape(_options.Application.Name)}[/].");
            _console.WriteLine();
            return;
        }

        _console.MarkupLine("A new version is available.");
        _console.WriteLine();
        WritePaddedLine("Current", $"[{ConsoleTheme.Muted.ToMarkup()}]{current}[/]", current.ToString().Length);
        WritePaddedLine("New", $"[bold]{next}[/]", next.ToString().Length);
        _console.WriteLine();
    }

    public async Task<T> RunWithProgressAsync<T>(
        string label,
        Func<IProgress<DownloadProgress>, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (!_console.Profile.Capabilities.Ansi)
        {
            // Redirected output or a very old terminal: a live progress bar would be noise.
            _console.MarkupLine($"{Markup.Escape(label)}...");
            return await work(new Progress<DownloadProgress>(), cancellationToken).ConfigureAwait(false);
        }

        var result = default(T)!;

        var columns = IsNarrow
            ? new ProgressColumn[] { new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn() }
            :
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn(),
            ];

        await _console.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(columns)
            .StartAsync(async context =>
            {
                var task = context.AddTask(Markup.Escape(label), autoStart: true, maxValue: 100);

                var progress = new Progress<DownloadProgress>(update =>
                {
                    if (update.TotalBytes is > 0)
                    {
                        task.MaxValue = update.TotalBytes.Value;
                        task.Value = update.BytesTransferred;
                    }
                    else
                    {
                        // No content length: never invent a percentage.
                        task.IsIndeterminate = true;
                    }
                });

                result = await work(progress, cancellationToken).ConfigureAwait(false);
                task.Value = task.MaxValue;
                task.StopTask();
            })
            .ConfigureAwait(false);

        return result;
    }

    public async Task<T> RunWithStatusAsync<T>(
        string label,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (!_console.Profile.Capabilities.Ansi)
        {
            _console.MarkupLine($"{Markup.Escape(label)}...");
            return await work(cancellationToken).ConfigureAwait(false);
        }

        return await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(_theme.Accent))
            .StartAsync(Markup.Escape(label), _ => work(cancellationToken))
            .ConfigureAwait(false);
    }

    public void ShowInformation(string message) =>
        _console.MarkupLine(Markup.Escape(message));

    public void ShowWarning(string message) =>
        _console.MarkupLine($"[{ConsoleTheme.Warning.ToMarkup()}]{Markup.Escape(message)}[/]");

    public void ShowSuccess(string message, string? detail = null)
    {
        _console.WriteLine();
        _console.MarkupLine($"[{ConsoleTheme.Success.ToMarkup()}]{Tick} {Markup.Escape(message)}[/]");

        if (!string.IsNullOrWhiteSpace(detail))
        {
            _console.MarkupLine($"[{ConsoleTheme.Muted.ToMarkup()}]{Markup.Escape(detail)}[/]");
        }
    }

    public void ShowFailure(PhoenixError error, string operationId, string? logDirectory, string? recoveryNote = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        _console.WriteLine();

        var colour = recoveryNote is null ? ConsoleTheme.Failure : ConsoleTheme.Warning;
        _console.MarkupLine($"[{colour.ToMarkup()}]{Markup.Escape(error.UserMessage)}[/]");

        if (!string.IsNullOrWhiteSpace(recoveryNote))
        {
            _console.WriteLine();
            _console.Write(new Markup(Markup.Escape(recoveryNote)));
            _console.WriteLine();
        }

        _console.WriteLine();
        _console.MarkupLine($"[{ConsoleTheme.Muted.ToMarkup()}]Reference: {Markup.Escape(operationId)}[/]");

        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            _console.MarkupLine(
                $"[{ConsoleTheme.Muted.ToMarkup()}]Details were saved to the Phoenix log.[/]");

            if (DiagnosticMode)
            {
                _console.MarkupLine($"[{ConsoleTheme.Muted.ToMarkup()}]{Markup.Escape(logDirectory)}[/]");
            }
        }

        if (DiagnosticMode)
        {
            _console.WriteLine();
            _console.MarkupLine($"[{ConsoleTheme.Muted.ToMarkup()}]{Markup.Escape(error.Code)}: " +
                                $"{Markup.Escape(error.TechnicalMessage)}[/]");
        }

        var support = BuildSupportLine();
        if (support is not null)
        {
            _console.WriteLine();
            _console.MarkupLine($"[{ConsoleTheme.Muted.ToMarkup()}]{Markup.Escape(support)}[/]");
        }

        _console.WriteLine();
    }

    private string? BuildSupportLine()
    {
        if (!string.IsNullOrWhiteSpace(_options.Company.SupportText))
        {
            return _options.Company.SupportText;
        }

        if (!string.IsNullOrWhiteSpace(_options.Company.SupportEmail))
        {
            return $"Need help? Contact {_options.Company.SupportEmail}.";
        }

        return string.IsNullOrWhiteSpace(_options.Company.SupportUrl)
            ? null
            : $"Need help? See {_options.Company.SupportUrl}.";
    }

    public void ShowReady(string applicationName, Uri url, bool browserOpened)
    {
        ArgumentNullException.ThrowIfNull(url);

        _console.WriteLine();
        _console.MarkupLine($"[bold]{Markup.Escape(applicationName)}[/] is ready.");

        _console.MarkupLine(browserOpened
            ? $"[{ConsoleTheme.Muted.ToMarkup()}]Opening your browser...[/]"
            : $"[{ConsoleTheme.Muted.ToMarkup()}]Open {Markup.Escape(url.ToString())} to get started.[/]");

        _console.WriteLine();
    }

    public void ShowDiagnostics(string title, IReadOnlyList<KeyValuePair<string, string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (!DiagnosticMode || rows.Count == 0)
        {
            return;
        }

        _console.WriteLine();
        _console.Write(new Rule($"[{ConsoleTheme.Muted.ToMarkup()}]{Markup.Escape(title)}[/]")
        {
            Justification = Justify.Left,
            Style = new Style(ConsoleTheme.Muted),
        });

        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn();

        foreach (var (key, value) in rows)
        {
            grid.AddRow(
                new Markup($"[{ConsoleTheme.Muted.ToMarkup()}]{Markup.Escape(key)}[/]"),
                new Markup(Markup.Escape(value ?? string.Empty)));
        }

        _console.Write(grid);
        _console.WriteLine();
    }

    public bool Confirm(string question, bool defaultValue)
    {
        if (!IsInteractive || _run.AssumeYes)
        {
            return defaultValue;
        }

        return _console.Confirm(Markup.Escape(question), defaultValue);
    }

    public void ShowRebootRequired(IReadOnlyList<string> componentNames)
    {
        ArgumentNullException.ThrowIfNull(componentNames);

        _console.WriteLine();
        _console.MarkupLine($"[{ConsoleTheme.Warning.ToMarkup()}]Your computer needs to restart.[/]");
        _console.WriteLine();

        if (componentNames.Count > 0)
        {
            _console.MarkupLine(componentNames.Count == 1
                ? $"{Markup.Escape(componentNames[0])} was installed and needs a restart to finish."
                : "These components were installed and need a restart to finish:");

            if (componentNames.Count > 1)
            {
                foreach (var name in componentNames)
                {
                    _console.MarkupLine($"  {Markup.Escape(name)}");
                }
            }
        }

        _console.WriteLine();
        _console.MarkupLine("Restart, then run this again to finish the installation.");
        _console.WriteLine();
    }

    /// <summary>
    /// Writes "label .......... status", right-aligning the status when there is room and
    /// falling back to a single space on a narrow terminal.
    /// </summary>
    private void WritePaddedLine(string escapedLabel, string markupRight, int plainRightLength)
    {
        var available = Width - plainRightLength - 1;
        var padding = available - escapedLabel.Length;

        if (padding < 1 || IsNarrow)
        {
            _console.MarkupLine($"{escapedLabel} {markupRight}");
            return;
        }

        _console.MarkupLine($"{escapedLabel}{new string(' ', padding)}{markupRight}");
    }

    /// <summary>One line of the narrative, completed in place.</summary>
    private sealed class Stage : IUiStage
    {
        private readonly SpectreUserInterface _ui;
        private readonly string _label;
        private bool _finished;

        public Stage(SpectreUserInterface ui, string label)
        {
            _ui = ui;
            _label = label;

            // Normally the label is written now and its outcome is appended to the same line
            // when the work finishes. In diagnostic mode the log sink writes to this console
            // too, and anything it emits in between would land in the middle of that line -
            // so there, the whole stage is written as one line once it completes.
            if (!_ui.DiagnosticMode)
            {
                _ui._console.Markup(Markup.Escape(label));
            }
        }

        public void Complete(string? detail = null)
        {
            var text = detail is null
                ? _ui.Tick
                : $"{_ui.Tick} {Markup.Escape(detail)}";

            Finish(text, ConsoleTheme.Success, detail is null ? _ui.Tick.Length : _ui.Tick.Length + 1 + detail.Length);
        }

        public void Warn(string detail) =>
            Finish($"! {Markup.Escape(detail)}", ConsoleTheme.Warning, detail.Length + 2);

        public void Fail(string? detail = null)
        {
            var text = detail is null ? _ui.Cross : $"{_ui.Cross} {Markup.Escape(detail)}";
            Finish(text, ConsoleTheme.Failure, detail is null ? _ui.Cross.Length : _ui.Cross.Length + 1 + detail.Length);
        }

        private void Finish(string markup, Color color, int plainLength)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;

            // In diagnostic mode the label was not written up front, so it is written here as
            // part of the same line.
            var prefix = _ui.DiagnosticMode ? Markup.Escape(_label) : string.Empty;

            var available = _ui.Width - plainLength - 1;
            var padding = available - _label.Length;

            if (padding < 1 || _ui.IsNarrow)
            {
                _ui._console.MarkupLine($"{prefix} [{color.ToMarkup()}]{markup}[/]");
                return;
            }

            _ui._console.MarkupLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix}{new string(' ', padding)}[{color.ToMarkup()}]{markup}[/]"));
        }

        public void Dispose() => Complete();
    }
}
