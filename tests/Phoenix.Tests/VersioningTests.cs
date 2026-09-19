using Phoenix.Core.Versioning;

namespace Phoenix.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("V10.0.0", 10, 0, 0, null)]
    [InlineData("1.2.3-qa.4", 1, 2, 3, "qa.4")]
    [InlineData("2.0.0-dev.12", 2, 0, 0, "dev.12")]
    [InlineData("9.0", 9, 0, 0, null)]
    [InlineData("1.2.3+build.7", 1, 2, 3, null)]
    public void Parses_supported_forms(string input, int major, int minor, int patch, string? preRelease)
    {
        Assert.True(SemanticVersion.TryParse(input, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(preRelease, version.PreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-version")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.-3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-qa..1")]
    [InlineData("1.2.3+")]
    public void Rejects_malformed_versions(string input) =>
        Assert.False(SemanticVersion.TryParse(input, out _));

    [Fact]
    public void Build_metadata_is_ignored_for_precedence()
    {
        var left = SemanticVersion.Parse("1.0.0+aaa");
        var right = SemanticVersion.Parse("1.0.0+bbb");

        Assert.Equal(0, left.CompareTo(right));
        Assert.True(left == right);
    }

    [Fact]
    public void Prerelease_sorts_below_its_release()
    {
        Assert.True(SemanticVersion.Parse("1.0.0-qa.1") < SemanticVersion.Parse("1.0.0"));
        Assert.True(SemanticVersion.Parse("1.0.0") > SemanticVersion.Parse("1.0.0-rc.9"));
    }

    [Fact]
    public void Orders_according_to_the_specification()
    {
        var expected = new[]
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
            "1.0.1",
            "1.1.0",
            "2.0.0",
        };

        var shuffled = expected.OrderBy(_ => Guid.NewGuid()).Select(SemanticVersion.Parse).ToList();
        var sorted = shuffled.Order().Select(v => v.ToString()).ToArray();

        Assert.Equal(expected, sorted);
    }

    [Fact]
    public void Numeric_prerelease_identifiers_compare_numerically()
    {
        Assert.True(SemanticVersion.Parse("1.0.0-qa.2") < SemanticVersion.Parse("1.0.0-qa.10"));
    }

    [Fact]
    public void Round_trips_through_ToString()
    {
        const string text = "2.1.0-qa.3";
        Assert.Equal(text, SemanticVersion.Parse(text).ToString());
    }

    [Fact]
    public void Exposes_the_prerelease_label_used_for_channels()
    {
        Assert.Equal("qa", SemanticVersion.Parse("1.0.0-QA.3").PreReleaseLabel);
        Assert.Null(SemanticVersion.Parse("1.0.0").PreReleaseLabel);
    }
}

public class VersionRangeTests
{
    [Theory]
    [InlineData("[9.0,11.0)", "9.0.0", true)]
    [InlineData("[9.0,11.0)", "10.5.1", true)]
    [InlineData("[9.0,11.0)", "11.0.0", false)]
    [InlineData("[9.0,11.0)", "8.9.9", false)]
    [InlineData("(1.0,2.0]", "1.0.0", false)]
    [InlineData("(1.0,2.0]", "2.0.0", true)]
    [InlineData("[3.12.4]", "3.12.4", true)]
    [InlineData("[3.12.4]", "3.12.5", false)]
    public void Interval_notation_is_honoured(string range, string version, bool expected)
    {
        Assert.True(VersionRange.TryParseInterval(range, out var parsed));
        Assert.Equal(expected, parsed.Satisfies(SemanticVersion.Parse(version)));
    }

    [Fact]
    public void Minimum_only_accepts_anything_newer()
    {
        Assert.True(VersionRange.TryCreate(null, "9.0.0", null, null, out var range));
        Assert.True(range.Satisfies(SemanticVersion.Parse("9.0.20")));
        Assert.True(range.Satisfies(SemanticVersion.Parse("12.0.0")));
        Assert.False(range.Satisfies(SemanticVersion.Parse("8.0.0")));
    }

    [Fact]
    public void Required_version_means_exactly_that_version()
    {
        Assert.True(VersionRange.TryCreate("3.12.4", null, null, null, out var range));
        Assert.True(range.Satisfies(SemanticVersion.Parse("3.12.4")));
        Assert.False(range.Satisfies(SemanticVersion.Parse("3.12.5")));
    }

    [Fact]
    public void Range_wins_over_the_individual_fields()
    {
        Assert.True(VersionRange.TryCreate("1.0.0", "5.0.0", "6.0.0", "[9.0,10.0)", out var range));
        Assert.True(range.Satisfies(SemanticVersion.Parse("9.5.0")));
        Assert.False(range.Satisfies(SemanticVersion.Parse("1.0.0")));
    }

    [Fact]
    public void Rejects_an_inverted_range()
    {
        Assert.False(VersionRange.TryCreate(null, "10.0.0", "9.0.0", null, out _));
    }

    [Fact]
    public void Rejects_unparseable_input()
    {
        Assert.False(VersionRange.TryCreate(null, "not-a-version", null, null, out _));
        Assert.False(VersionRange.TryParseInterval("9.0,10.0", out _));
    }

    [Fact]
    public void Null_never_satisfies_a_range() =>
        Assert.False(VersionRange.AtLeast(SemanticVersion.Parse("1.0.0")).Satisfies(null));
}
