using System.Security;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Models;
using Phoenix.Core.Versioning;

namespace Phoenix.Infrastructure.Prerequisites.Detectors;

/// <summary>Reads the version resource of a file on disk.</summary>
public sealed class FileVersionDetector : IPrerequisiteDetector
{
    public DetectionStrategy Strategy => DetectionStrategy.FileVersion;

    public Task<DetectionResult> DetectAsync(PrerequisiteDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var path = ExecutableResolver.Resolve(definition.Detection.Path);
        if (path is null)
        {
            return Task.FromResult(DetectionResult.NotFound($"'{definition.Detection.Path}' does not exist."));
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var text = info.ProductVersion ?? info.FileVersion;

            if (text is null || !SemanticVersion.TryParse(CleanVersion(text), out var version))
            {
                return Task.FromResult(DetectionResult.NotFound(
                    $"'{path}' does not carry a readable version ({text ?? "none"})."));
            }

            return Task.FromResult(DetectionResult.Found(version, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(DetectionResult.NotFound($"Could not read '{path}': {ex.Message}"));
        }
    }

    /// <summary>File versions are often "9.0.20.12345" or "v14.38"; keep the first three parts.</summary>
    internal static string CleanVersion(string value)
    {
        var text = value.Trim();
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
        {
            text = text[..plus];
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0)
        {
            text = text[..space];
        }

        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var parts = text.Split('.');
        return parts.Length <= 3 ? text : string.Join('.', parts.Take(3));
    }
}

/// <summary>Reads a version from the Windows registry.</summary>
public sealed class RegistryDetector : IPrerequisiteDetector
{
    private readonly ILogger<RegistryDetector> _logger;

    public RegistryDetector(ILogger<RegistryDetector> logger) => _logger = logger;

    public DetectionStrategy Strategy => DetectionStrategy.Registry;

    public Task<DetectionResult> DetectAsync(PrerequisiteDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(DetectionResult.NotFound("Registry detection requires Windows."));
        }

        var detection = definition.Detection;

        try
        {
            var hive = string.Equals(detection.RegistryHive, "HKCU", StringComparison.OrdinalIgnoreCase)
                ? RegistryHive.CurrentUser
                : RegistryHive.LocalMachine;

            var view = detection.RegistryWow6432 ? RegistryView.Registry32 : RegistryView.Registry64;

            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(detection.RegistryKey!);

            if (key is null)
            {
                return Task.FromResult(DetectionResult.NotFound(
                    $"Registry key '{detection.RegistryHive ?? "HKLM"}\\{detection.RegistryKey}' does not exist."));
            }

            var value = key.GetValue(detection.RegistryValue);
            if (value is null)
            {
                return Task.FromResult(DetectionResult.NotFound(
                    $"Registry value '{detection.RegistryValue}' is not set."));
            }

            var text = value switch
            {
                string s => s,
                int i => i.ToString(CultureInfo.InvariantCulture),
                long l => l.ToString(CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };

            var extracted = CommandVersionDetector.ExtractVersion(text, detection.VersionPattern)
                            ?? (SemanticVersion.TryParse(FileVersionDetector.CleanVersion(text), out var parsed)
                                ? parsed
                                : null);

            if (extracted is null)
            {
                return Task.FromResult(DetectionResult.NotFound(
                    $"Registry value '{detection.RegistryValue}' contains '{text}', which is not a version."));
            }

            return Task.FromResult(DetectionResult.Found(
                extracted,
                $"{detection.RegistryHive ?? "HKLM"}\\{detection.RegistryKey}\\{detection.RegistryValue}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogDebug(ex, "Registry detection for {Id} failed.", definition.Id);
            return Task.FromResult(DetectionResult.NotFound($"Could not read the registry: {ex.Message}"));
        }
    }
}

/// <summary>Always reports missing. Exists so the install path can be demonstrated and tested.</summary>
public sealed class AssumeMissingDetector : IPrerequisiteDetector
{
    public DetectionStrategy Strategy => DetectionStrategy.AssumeMissing;

    public Task<DetectionResult> DetectAsync(PrerequisiteDefinition definition, CancellationToken cancellationToken) =>
        Task.FromResult(DetectionResult.NotFound("Configured to always report missing."));
}

/// <summary>Always reports satisfied. Useful when a component is guaranteed by machine policy.</summary>
public sealed class AssumePresentDetector : IPrerequisiteDetector
{
    public DetectionStrategy Strategy => DetectionStrategy.AssumePresent;

    public Task<DetectionResult> DetectAsync(PrerequisiteDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var version = SemanticVersion.TryParse(definition.MinimumVersion ?? definition.RequiredVersion, out var parsed)
            ? parsed
            : new SemanticVersion(0, 0, 0);

        return Task.FromResult(DetectionResult.Found(version, "Configured to always report present."));
    }
}
