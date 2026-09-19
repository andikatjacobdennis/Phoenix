using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Models;
using Phoenix.Core.Versioning;
using Phoenix.Infrastructure.Processes;

namespace Phoenix.Infrastructure.Prerequisites.Detectors;

/// <summary>
/// Runs a program and reads a version out of its output - the way you would check Python or
/// Node by hand. Used for both <see cref="DetectionStrategy.ExecutableVersion"/> and
/// <see cref="DetectionStrategy.Command"/>; the difference is only intent.
/// </summary>
public sealed class CommandVersionDetector : IPrerequisiteDetector
{
    /// <summary>Matches "3.12.4", "v20.11.0", "Python 3.12.4" and similar.</summary>
    private static readonly Regex DefaultVersionPattern = new(
        @"(?<version>\d+\.\d+(\.\d+)?(-[0-9A-Za-z.-]+)?)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private readonly ILogger<CommandVersionDetector> _logger;

    public CommandVersionDetector(ILogger<CommandVersionDetector> logger) => _logger = logger;

    public DetectionStrategy Strategy => DetectionStrategy.ExecutableVersion;

    public async Task<DetectionResult> DetectAsync(
        PrerequisiteDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var detection = definition.Detection;
        var executable = ExecutableResolver.Resolve(detection.Path);

        if (executable is null)
        {
            return DetectionResult.NotFound($"'{detection.Path}' was not found on this machine.");
        }

        var arguments = SplitArguments(detection.Arguments ?? "--version");

        var result = await ProcessRunner.RunAsync(
                executable,
                arguments,
                TimeSpan.FromSeconds(Math.Max(1, detection.TimeoutSeconds)),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.TimedOut)
        {
            return DetectionResult.NotFound($"'{executable}' did not respond within the detection timeout.");
        }

        // Some tools report their version on stderr and exit non-zero; the output matters
        // more than the exit code here.
        var output = result.CombinedOutput;
        if (string.IsNullOrWhiteSpace(output))
        {
            return DetectionResult.NotFound($"'{executable}' produced no output (exit code {result.ExitCode}).");
        }

        var version = ExtractVersion(output, detection.VersionPattern);
        if (version is null)
        {
            _logger.LogDebug("Could not read a version from '{Executable}' output: {Output}", executable, output.Trim());
            return DetectionResult.NotFound($"Could not read a version from the output of '{executable}'.");
        }

        return DetectionResult.Found(version, executable);
    }

    internal static SemanticVersion? ExtractVersion(string output, string? pattern)
    {
        var regex = string.IsNullOrWhiteSpace(pattern)
            ? DefaultVersionPattern
            : new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        try
        {
            var match = regex.Match(output);
            if (!match.Success)
            {
                return null;
            }

            var group = match.Groups["version"];
            var value = group.Success ? group.Value : match.Value;

            return SemanticVersion.TryParse(value, out var version) ? version : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Splits a configured argument string, honouring double quotes so a path with spaces
    /// stays one argument.
    /// </summary>
    internal static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in arguments)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;

                case ' ' when !inQuotes:
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }

                    break;

                default:
                    current.Append(c);
                    break;
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}

/// <summary>The same mechanism, registered for <see cref="DetectionStrategy.Command"/>.</summary>
public sealed class ArbitraryCommandDetector : IPrerequisiteDetector
{
    private readonly CommandVersionDetector _inner;

    public ArbitraryCommandDetector(CommandVersionDetector inner) => _inner = inner;

    public DetectionStrategy Strategy => DetectionStrategy.Command;

    public Task<DetectionResult> DetectAsync(
        PrerequisiteDefinition definition,
        CancellationToken cancellationToken) =>
        _inner.DetectAsync(definition, cancellationToken);
}
