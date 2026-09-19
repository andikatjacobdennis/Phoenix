namespace Phoenix.Core.Models;

/// <summary>
/// Distribution channel. A Production installation must never pick up a Development build,
/// so the channel of a release is resolved explicitly rather than guessed from "latest".
/// </summary>
public enum ReleaseChannel
{
    Development = 0,
    QA = 1,
    Production = 2,
}

public static class ReleaseChannelExtensions
{
    /// <summary>
    /// The prerelease label that identifies each channel in a release tag,
    /// e.g. <c>v2.1.0-qa.3</c> belongs to <see cref="ReleaseChannel.QA"/>.
    /// </summary>
    public static string? PreReleaseLabel(this ReleaseChannel channel) => channel switch
    {
        ReleaseChannel.Development => "dev",
        ReleaseChannel.QA => "qa",
        _ => null,
    };

    public static string DisplayName(this ReleaseChannel channel) => channel switch
    {
        ReleaseChannel.Development => "Development",
        ReleaseChannel.QA => "QA",
        _ => "Production",
    };
}
