using Phoenix.Core.Configuration;
using Phoenix.Core.Models;
using Phoenix.Core.Versioning;

namespace Phoenix.Core.Releases;

/// <summary>
/// Chooses which releases are worth attempting, in order. Pure logic with no IO, because
/// picking the wrong release is the most expensive mistake Phoenix can make.
/// </summary>
public sealed class ReleaseSelector
{
    /// <summary>
    /// Filters releases down to the ones this machine is allowed to install, newest first,
    /// bounded by <see cref="UpdateOptions.RemoteFallbackCount"/>.
    /// </summary>
    public IReadOnlyList<ReleaseCandidate> SelectCandidates(
        IReadOnlyList<ReleaseInfo> releases,
        UpdateOptions updates,
        IReadOnlyCollection<string> rejectedVersions,
        string manifestFileName = ReleaseManifest.DefaultFileName)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(rejectedVersions);

        SemanticVersion? pinned = null;
        if (updates.Policy == UpdatePolicy.Pinned &&
            SemanticVersion.TryParse(updates.PinnedVersion, out var pinnedVersion))
        {
            pinned = pinnedVersion;
        }

        var candidates = new List<ReleaseCandidate>();

        foreach (var release in releases)
        {
            if (release.IsDraft)
            {
                continue;
            }

            // A tag and a prerelease flag that disagree mean a broken publish pipeline.
            if (!ChannelResolver.IsConsistent(release))
            {
                continue;
            }

            if (!ChannelResolver.IsInstallableOn(ChannelResolver.FromVersion(release.Version), updates.Channel))
            {
                continue;
            }

            if (pinned is not null && release.Version != pinned)
            {
                continue;
            }

            if (rejectedVersions.Contains(release.Version.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var manifest = release.FindAsset(manifestFileName);
            if (manifest is null)
            {
                // Without a manifest Phoenix would have to guess which asset to install,
                // and guessing is how installers execute the wrong binary.
                continue;
            }

            candidates.Add(new ReleaseCandidate { Release = release, ManifestAsset = manifest });
        }

        var ordered = candidates
            .OrderByDescending(c => c.Version)
            .ThenByDescending(c => c.Release.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();

        var limit = pinned is not null ? 1 : Math.Max(1, updates.RemoteFallbackCount + 1);
        return ordered.Count <= limit ? ordered : ordered.GetRange(0, limit);
    }

    /// <summary>Decides what to do with the installed version and the available candidates.</summary>
    public UpdateDecision Decide(
        InstallationInfo? installed,
        IReadOnlyList<ReleaseCandidate> candidates,
        UpdateOptions updates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(updates);

        var newest = candidates.Count > 0 ? candidates[0] : null;

        if (installed is null)
        {
            return newest is null
                ? new UpdateDecision
                {
                    Action = UpdateAction.NoReleaseAvailable,
                    Explanation = "Nothing is installed and no suitable release was found.",
                }
                : new UpdateDecision
                {
                    Action = UpdateAction.FreshInstall,
                    Candidates = candidates,
                    Explanation = $"First installation of {newest.Version}.",
                };
        }

        if (newest is null)
        {
            return new UpdateDecision
            {
                Action = UpdateAction.NoReleaseAvailable,
                Installed = installed,
                Explanation = "No suitable release was found; keeping the installed version.",
            };
        }

        if (updates.Policy == UpdatePolicy.Pinned)
        {
            return newest.Version == installed.Version
                ? UpToDate(installed, $"Pinned to {newest.Version}, which is installed.")
                : new UpdateDecision
                {
                    Action = UpdateAction.Update,
                    Installed = installed,
                    Candidates = candidates,
                    Explanation = $"Pinned to {newest.Version}.",
                };
        }

        var comparison = newest.Version.CompareTo(installed.Version);

        if (comparison == 0)
        {
            return UpToDate(installed, $"{installed.Version} is current.");
        }

        if (comparison < 0 && !updates.AllowDowngrade)
        {
            return UpToDate(installed, $"Installed {installed.Version} is newer than the latest release {newest.Version}.");
        }

        if (updates.Policy == UpdatePolicy.CheckOnly)
        {
            return new UpdateDecision
            {
                Action = UpdateAction.UpdateAvailableButBlocked,
                Installed = installed,
                Candidates = candidates,
                Explanation = $"{newest.Version} is available but the update policy is CheckOnly.",
            };
        }

        return new UpdateDecision
        {
            Action = UpdateAction.Update,
            Installed = installed,
            Candidates = candidates,
            Explanation = $"Updating from {installed.Version} to {newest.Version}.",
        };
    }

    private static UpdateDecision UpToDate(InstallationInfo installed, string explanation) => new()
    {
        Action = UpdateAction.UpToDate,
        Installed = installed,
        Explanation = explanation,
    };
}
