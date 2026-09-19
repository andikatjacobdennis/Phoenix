using Phoenix.Core.Models;
using Phoenix.Core.Versioning;

namespace Phoenix.Core.Releases;

/// <summary>
/// Decides which channel a release belongs to.
///
/// The rule is deliberately boring and deterministic, because getting it wrong means a
/// Production machine installing a Development build:
///
/// <list type="bullet">
///   <item>No prerelease label (<c>v1.4.0</c>) means Production.</item>
///   <item>Label <c>qa</c> (<c>v1.5.0-qa.2</c>), and also <c>beta</c>/<c>rc</c>, mean QA.</item>
///   <item>Label <c>dev</c> (<c>v1.6.0-dev.7</c>), and also <c>alpha</c>/<c>ci</c>, mean Development.</item>
///   <item>Any other label is treated as Development: unknown means least trusted.</item>
/// </list>
///
/// The manifest carries the channel too. When both exist they must agree, otherwise the
/// release is rejected rather than guessed at.
/// </summary>
public static class ChannelResolver
{
    public static ReleaseChannel FromVersion(SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (!version.IsPreRelease)
        {
            return ReleaseChannel.Production;
        }

        return version.PreReleaseLabel switch
        {
            "qa" or "beta" or "rc" => ReleaseChannel.QA,
            _ => ReleaseChannel.Development,
        };
    }

    /// <summary>
    /// True when a release published in <paramref name="releaseChannel"/> may be installed by a
    /// machine configured for <paramref name="configuredChannel"/>. Channels do not inherit:
    /// a Production machine takes Production releases only.
    /// </summary>
    public static bool IsInstallableOn(ReleaseChannel releaseChannel, ReleaseChannel configuredChannel) =>
        releaseChannel == configuredChannel;

    /// <summary>
    /// Cross-checks the tag against the source's own prerelease flag. A stable tag published as a
    /// GitHub prerelease (or the reverse) is a mistake somewhere in the pipeline, and Phoenix
    /// refuses to interpret it.
    /// </summary>
    public static bool IsConsistent(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return release.Version.IsPreRelease == release.IsPrerelease;
    }
}
