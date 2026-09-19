using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Core.Versioning;

namespace Phoenix.Core.Releases;

/// <summary>Context for validating a manifest against what this machine is configured to install.</summary>
public sealed record ManifestValidationContext
{
    public required string ExpectedApplicationId { get; init; }

    public required ReleaseChannel ExpectedChannel { get; init; }

    public required SemanticVersion ReleaseVersion { get; init; }

    public required string OperatingSystem { get; init; }

    public required string Architecture { get; init; }

    public required SemanticVersion PhoenixVersion { get; init; }

    public bool RequireChecksum { get; init; } = true;
}

/// <summary>
/// A release manifest is remote content, so it is treated as hostile until proven otherwise.
/// Nothing is downloaded, extracted or executed on the strength of an unvalidated manifest.
/// </summary>
public static class ManifestValidator
{
    private const int MaxSupportedSchemaVersion = 1;

    public static Result<ManifestPackage> Validate(ReleaseManifest? manifest, ManifestValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (manifest is null)
        {
            return PhoenixError.Verification("PX-MANIFEST-EMPTY", "The release manifest was empty or unreadable.");
        }

        if (manifest.SchemaVersion is < 1 or > MaxSupportedSchemaVersion)
        {
            return PhoenixError.Verification(
                "PX-MANIFEST-SCHEMA",
                $"Manifest schema version {manifest.SchemaVersion} is not supported by this Phoenix build " +
                $"(supported: 1..{MaxSupportedSchemaVersion}).");
        }

        if (!string.Equals(manifest.ApplicationId, context.ExpectedApplicationId, StringComparison.OrdinalIgnoreCase))
        {
            // The strongest check we have: this package is not for this product.
            return PhoenixError.Security(
                "PX-MANIFEST-APPID",
                $"Manifest declares application '{manifest.ApplicationId}' but this installation is configured " +
                $"for '{context.ExpectedApplicationId}'.");
        }

        if (!SemanticVersion.TryParse(manifest.Version, out var manifestVersion))
        {
            return PhoenixError.Verification(
                "PX-MANIFEST-VERSION",
                $"Manifest version '{manifest.Version}' is not a valid semantic version.");
        }

        if (manifestVersion != context.ReleaseVersion)
        {
            return PhoenixError.Security(
                "PX-MANIFEST-VERSION-MISMATCH",
                $"Manifest version {manifestVersion} does not match the release tag version {context.ReleaseVersion}.");
        }

        if (manifest.Channel != context.ExpectedChannel)
        {
            return PhoenixError.Security(
                "PX-MANIFEST-CHANNEL",
                $"Manifest channel '{manifest.Channel}' does not match the configured channel " +
                $"'{context.ExpectedChannel}'.");
        }

        var tagChannel = ChannelResolver.FromVersion(manifestVersion);
        if (tagChannel != manifest.Channel)
        {
            return PhoenixError.Security(
                "PX-MANIFEST-CHANNEL-TAG",
                $"Manifest declares channel '{manifest.Channel}' but version {manifestVersion} belongs to " +
                $"channel '{tagChannel}'.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.MinimumPhoenixVersion))
        {
            if (!SemanticVersion.TryParse(manifest.MinimumPhoenixVersion, out var minimumPhoenix))
            {
                return PhoenixError.Verification(
                    "PX-MANIFEST-MINPHOENIX",
                    $"Manifest minimumPhoenixVersion '{manifest.MinimumPhoenixVersion}' is not a valid semantic version.");
            }

            // A prerelease build of Phoenix counts as its release version for this gate: a
            // 1.2.0-dev build of the installer can install a release that asks for 1.2.0.
            // Release *ordering* still uses full semantic precedence; this is only a floor.
            var effectivePhoenixVersion = new SemanticVersion(
                context.PhoenixVersion.Major,
                context.PhoenixVersion.Minor,
                context.PhoenixVersion.Patch);

            if (effectivePhoenixVersion < minimumPhoenix)
            {
                return PhoenixError.Verification(
                    "PX-MANIFEST-PHOENIX-TOO-OLD",
                    $"Release {manifestVersion} requires Phoenix {minimumPhoenix} or newer; this is " +
                    $"{context.PhoenixVersion}.");
            }
        }

        var package = manifest.FindPackage(context.OperatingSystem, context.Architecture);
        if (package is null)
        {
            return PhoenixError.Verification(
                "PX-MANIFEST-NO-PACKAGE",
                $"Release {manifestVersion} has no package for {context.OperatingSystem}-{context.Architecture}.");
        }

        return ValidatePackage(package, manifestVersion, context);
    }

    private static Result<ManifestPackage> ValidatePackage(
        ManifestPackage package,
        SemanticVersion version,
        ManifestValidationContext context)
    {
        if (!IsSafeFileName(package.PackageFile))
        {
            return PhoenixError.Security(
                "PX-MANIFEST-PACKAGE-NAME",
                $"Manifest package file name '{package.PackageFile}' is not a plain file name.");
        }

        if (!string.Equals(package.PackageFormat, "zip", StringComparison.OrdinalIgnoreCase))
        {
            return PhoenixError.Verification(
                "PX-MANIFEST-FORMAT",
                $"Package format '{package.PackageFormat}' is not supported (expected 'zip').");
        }

        if (context.RequireChecksum && !IsSha256(package.Sha256))
        {
            return PhoenixError.Security(
                "PX-MANIFEST-SHA",
                $"Manifest for {version} does not contain a valid SHA-256 for '{package.PackageFile}'.");
        }

        if (string.IsNullOrWhiteSpace(package.Executable) || !IsSafeRelativePath(package.Executable))
        {
            // Never run an arbitrary path chosen by remote content.
            return PhoenixError.Security(
                "PX-MANIFEST-EXECUTABLE",
                $"Manifest executable '{package.Executable}' must be a relative path inside the package.");
        }

        if (package.HealthEndpoint is { Length: > 0 } endpoint && !endpoint.StartsWith('/'))
        {
            return PhoenixError.Verification(
                "PX-MANIFEST-HEALTH",
                $"Manifest health endpoint '{endpoint}' must start with '/'.");
        }

        if (package.SizeBytes < 0)
        {
            return PhoenixError.Verification("PX-MANIFEST-SIZE", "Manifest package size cannot be negative.");
        }

        return package;
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    /// <summary>A plain file name: no directory separators, no drive, no traversal.</summary>
    public static bool IsSafeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
        {
            return false;
        }

        if (value.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            return false;
        }

        return value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>A relative path that cannot escape the package root.</summary>
    public static bool IsSafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Path.IsPathRooted(value) || value.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = value.Split(['/', '\\'], StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }
        }

        return true;
    }
}
