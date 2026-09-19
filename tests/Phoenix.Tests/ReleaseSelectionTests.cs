using Phoenix.Core.Configuration;
using Phoenix.Core.Models;
using Phoenix.Core.Releases;
using Phoenix.Core.Versioning;

namespace Phoenix.Tests;

public class ChannelResolverTests
{
    [Theory]
    [InlineData("1.4.0", ReleaseChannel.Production)]
    [InlineData("1.5.0-qa.2", ReleaseChannel.QA)]
    [InlineData("1.5.0-beta.1", ReleaseChannel.QA)]
    [InlineData("1.5.0-rc.1", ReleaseChannel.QA)]
    [InlineData("1.6.0-dev.7", ReleaseChannel.Development)]
    [InlineData("1.6.0-alpha.1", ReleaseChannel.Development)]
    [InlineData("1.6.0-something", ReleaseChannel.Development)]
    public void Maps_tags_to_channels(string version, ReleaseChannel expected) =>
        Assert.Equal(expected, ChannelResolver.FromVersion(SemanticVersion.Parse(version)));

    [Fact]
    public void Channels_do_not_inherit_from_each_other()
    {
        Assert.False(ChannelResolver.IsInstallableOn(ReleaseChannel.Development, ReleaseChannel.Production));
        Assert.False(ChannelResolver.IsInstallableOn(ReleaseChannel.QA, ReleaseChannel.Production));
        Assert.False(ChannelResolver.IsInstallableOn(ReleaseChannel.Production, ReleaseChannel.QA));
        Assert.True(ChannelResolver.IsInstallableOn(ReleaseChannel.QA, ReleaseChannel.QA));
    }

    [Fact]
    public void A_stable_tag_published_as_a_prerelease_is_inconsistent()
    {
        var release = TestReleases.Create("v1.0.0", prerelease: true);
        Assert.False(ChannelResolver.IsConsistent(release));
    }

    [Fact]
    public void A_prerelease_tag_flagged_as_prerelease_is_consistent()
    {
        var release = TestReleases.Create("v1.0.0-qa.1", prerelease: true);
        Assert.True(ChannelResolver.IsConsistent(release));
    }
}

public class ReleaseSelectorTests
{
    private readonly ReleaseSelector _selector = new();

    [Fact]
    public void Picks_production_releases_for_a_production_machine()
    {
        var releases = new[]
        {
            TestReleases.Create("v2.0.0-dev.1", prerelease: true),
            TestReleases.Create("v1.9.0-qa.3", prerelease: true),
            TestReleases.Create("v1.8.0"),
            TestReleases.Create("v1.7.0"),
        };

        var candidates = _selector.SelectCandidates(
            releases,
            new UpdateOptions { Channel = ReleaseChannel.Production, RemoteFallbackCount = 2 },
            []);

        Assert.Equal(["v1.8.0", "v1.7.0"], candidates.Select(c => c.Release.Tag));
    }

    [Fact]
    public void Picks_qa_releases_for_a_qa_machine()
    {
        var releases = new[]
        {
            TestReleases.Create("v1.9.0-qa.3", prerelease: true),
            TestReleases.Create("v1.8.0"),
        };

        var candidates = _selector.SelectCandidates(
            releases,
            new UpdateOptions { Channel = ReleaseChannel.QA },
            []);

        Assert.Single(candidates);
        Assert.Equal("v1.9.0-qa.3", candidates[0].Release.Tag);
    }

    [Fact]
    public void Orders_newest_first_and_bounds_the_fallback_list()
    {
        var releases = new[]
        {
            TestReleases.Create("v1.0.0"),
            TestReleases.Create("v1.3.0"),
            TestReleases.Create("v1.2.0"),
            TestReleases.Create("v1.1.0"),
        };

        var candidates = _selector.SelectCandidates(
            releases,
            new UpdateOptions { RemoteFallbackCount = 1 },
            []);

        Assert.Equal(["v1.3.0", "v1.2.0"], candidates.Select(c => c.Release.Tag));
    }

    [Fact]
    public void Skips_drafts_releases_without_a_manifest_and_inconsistent_tags()
    {
        var releases = new[]
        {
            TestReleases.Create("v1.5.0", draft: true),
            TestReleases.Create("v1.4.0", includeManifest: false),
            TestReleases.Create("v1.3.0", prerelease: true),
            TestReleases.Create("v1.2.0"),
        };

        var candidates = _selector.SelectCandidates(releases, new UpdateOptions(), []);

        Assert.Single(candidates);
        Assert.Equal("v1.2.0", candidates[0].Release.Tag);
    }

    [Fact]
    public void Skips_versions_that_already_failed()
    {
        var releases = new[] { TestReleases.Create("v1.2.0"), TestReleases.Create("v1.1.0") };

        var candidates = _selector.SelectCandidates(releases, new UpdateOptions(), ["1.2.0"]);

        Assert.Single(candidates);
        Assert.Equal("v1.1.0", candidates[0].Release.Tag);
    }

    [Fact]
    public void A_pinned_version_is_the_only_candidate()
    {
        var releases = new[]
        {
            TestReleases.Create("v1.3.0"),
            TestReleases.Create("v1.2.0"),
            TestReleases.Create("v1.1.0"),
        };

        var candidates = _selector.SelectCandidates(
            releases,
            new UpdateOptions { Policy = UpdatePolicy.Pinned, PinnedVersion = "1.2.0", RemoteFallbackCount = 5 },
            []);

        Assert.Single(candidates);
        Assert.Equal("v1.2.0", candidates[0].Release.Tag);
    }

    [Fact]
    public void Nothing_installed_means_a_fresh_install()
    {
        var candidates = _selector.SelectCandidates([TestReleases.Create("v1.0.0")], new UpdateOptions(), []);
        var decision = _selector.Decide(null, candidates, new UpdateOptions());

        Assert.Equal(UpdateAction.FreshInstall, decision.Action);
        Assert.Equal("1.0.0", decision.Target!.Version.ToString());
    }

    [Fact]
    public void Nothing_installed_and_no_releases_is_reported_as_such()
    {
        var decision = _selector.Decide(null, [], new UpdateOptions());
        Assert.Equal(UpdateAction.NoReleaseAvailable, decision.Action);
    }

    [Fact]
    public void A_newer_release_is_an_update()
    {
        var candidates = _selector.SelectCandidates([TestReleases.Create("v1.1.0")], new UpdateOptions(), []);
        var decision = _selector.Decide(TestReleases.Installed("1.0.0"), candidates, new UpdateOptions());

        Assert.Equal(UpdateAction.Update, decision.Action);
    }

    [Fact]
    public void The_same_version_is_up_to_date()
    {
        var candidates = _selector.SelectCandidates([TestReleases.Create("v1.0.0")], new UpdateOptions(), []);
        var decision = _selector.Decide(TestReleases.Installed("1.0.0"), candidates, new UpdateOptions());

        Assert.Equal(UpdateAction.UpToDate, decision.Action);
    }

    [Fact]
    public void An_older_release_does_not_downgrade_a_newer_installation()
    {
        var candidates = _selector.SelectCandidates([TestReleases.Create("v1.0.0")], new UpdateOptions(), []);
        var decision = _selector.Decide(TestReleases.Installed("1.1.0"), candidates, new UpdateOptions());

        Assert.Equal(UpdateAction.UpToDate, decision.Action);
    }

    [Fact]
    public void Downgrades_are_allowed_when_configuration_says_so()
    {
        var candidates = _selector.SelectCandidates([TestReleases.Create("v1.0.0")], new UpdateOptions(), []);
        var decision = _selector.Decide(
            TestReleases.Installed("1.1.0"),
            candidates,
            new UpdateOptions { AllowDowngrade = true });

        Assert.Equal(UpdateAction.Update, decision.Action);
    }

    [Fact]
    public void CheckOnly_reports_the_update_without_installing_it()
    {
        var options = new UpdateOptions { Policy = UpdatePolicy.CheckOnly };
        var candidates = _selector.SelectCandidates([TestReleases.Create("v1.1.0")], options, []);
        var decision = _selector.Decide(TestReleases.Installed("1.0.0"), candidates, options);

        Assert.Equal(UpdateAction.UpdateAvailableButBlocked, decision.Action);
    }
}

internal static class TestReleases
{
    public static ReleaseInfo Create(
        string tag,
        bool prerelease = false,
        bool draft = false,
        bool includeManifest = true)
    {
        var version = SemanticVersion.Parse(tag);

        var assets = new List<ReleaseAsset>
        {
            new()
            {
                Name = "phoenix-demoapp-win-x64.zip",
                DownloadUrl = new Uri($"https://example.invalid/{tag}/package.zip"),
                SizeBytes = 1024,
            },
        };

        if (includeManifest)
        {
            assets.Add(new ReleaseAsset
            {
                Name = ReleaseManifest.DefaultFileName,
                DownloadUrl = new Uri($"https://example.invalid/{tag}/release-manifest.json"),
                SizeBytes = 512,
            });
        }

        return new ReleaseInfo
        {
            Tag = tag,
            Version = version,
            IsPrerelease = prerelease || version.IsPreRelease,
            IsDraft = draft,
            PublishedAt = DateTimeOffset.UnixEpoch.AddDays(version.Major * 100 + version.Minor),
            Assets = assets,
        };
    }

    public static InstallationInfo Installed(string version) => new()
    {
        Version = SemanticVersion.Parse(version),
        Path = Path.Combine(Path.GetTempPath(), "phoenix-tests", version),
        RelativeExecutable = "App.exe",
    };
}
