using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.Core.Releases;
using Phoenix.Core.Versioning;

namespace Phoenix.Tests;

public class ManifestValidatorTests
{
    private static ManifestValidationContext Context(string version = "1.2.0") => new()
    {
        ExpectedApplicationId = "phoenix-demoapp",
        ExpectedChannel = ReleaseChannel.Production,
        ReleaseVersion = SemanticVersion.Parse(version),
        OperatingSystem = "win",
        Architecture = "x64",
        PhoenixVersion = SemanticVersion.Parse("1.0.0"),
    };

    private static ReleaseManifest Manifest(Action<ManifestBuilder>? configure = null)
    {
        var builder = new ManifestBuilder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public void Accepts_a_well_formed_manifest()
    {
        var result = ManifestValidator.Validate(Manifest(), Context());

        Assert.True(result.IsSuccess);
        Assert.Equal("Phoenix.DemoApp.exe", result.Value.Executable);
    }

    [Fact]
    public void Rejects_a_manifest_for_a_different_application()
    {
        var result = ManifestValidator.Validate(
            Manifest(b => b.ApplicationId = "someone-elses-app"),
            Context());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Security, result.Error.Category);
        Assert.Equal("PX-MANIFEST-APPID", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_manifest_whose_version_does_not_match_the_tag()
    {
        var result = ManifestValidator.Validate(Manifest(b => b.Version = "9.9.9"), Context("1.2.0"));

        Assert.True(result.IsFailure);
        Assert.Equal("PX-MANIFEST-VERSION-MISMATCH", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_release_from_another_channel()
    {
        var result = ManifestValidator.Validate(
            Manifest(b =>
            {
                b.Version = "1.2.0-qa.1";
                b.Channel = ReleaseChannel.QA;
            }),
            Context("1.2.0-qa.1"));

        Assert.True(result.IsFailure);
        Assert.Equal("PX-MANIFEST-CHANNEL", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_manifest_whose_channel_contradicts_its_own_version()
    {
        // Claims Production, but 1.2.0-qa.1 belongs to QA.
        var result = ManifestValidator.Validate(
            Manifest(b =>
            {
                b.Version = "1.2.0-qa.1";
                b.Channel = ReleaseChannel.Production;
            }),
            Context("1.2.0-qa.1"));

        Assert.True(result.IsFailure);
        Assert.Equal("PX-MANIFEST-CHANNEL-TAG", result.Error.Code);
    }

    [Theory]
    [InlineData("..\\..\\Windows\\System32\\cmd.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("/usr/bin/sh")]
    [InlineData("..")]
    [InlineData("")]
    public void Rejects_an_executable_that_escapes_the_package(string executable)
    {
        var result = ManifestValidator.Validate(Manifest(b => b.Executable = executable), Context());

        Assert.True(result.IsFailure);
        Assert.Equal("PX-MANIFEST-EXECUTABLE", result.Error.Code);
    }

    [Theory]
    [InlineData("../package.zip")]
    [InlineData("sub/package.zip")]
    [InlineData("C:\\package.zip")]
    public void Rejects_a_package_file_that_is_not_a_plain_name(string packageFile)
    {
        var result = ManifestValidator.Validate(Manifest(b => b.PackageFile = packageFile), Context());

        Assert.True(result.IsFailure);
        Assert.Equal("PX-MANIFEST-PACKAGE-NAME", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_missing_or_malformed_checksum()
    {
        var result = ManifestValidator.Validate(Manifest(b => b.Sha256 = "not-a-hash"), Context());

        Assert.True(result.IsFailure);
        Assert.Equal("PX-MANIFEST-SHA", result.Error.Code);
    }

    [Fact]
    public void Accepts_a_missing_checksum_only_when_configured_to()
    {
        var context = Context() with { RequireChecksum = false };
        var result = ManifestValidator.Validate(Manifest(b => b.Sha256 = string.Empty), context);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Rejects_a_release_with_no_package_for_this_machine()
    {
        var result = ManifestValidator.Validate(
            Manifest(b => b.Architecture = "arm64"),
            Context());

        Assert.True(result.IsFailure);
        Assert.Equal("PX-MANIFEST-NO-PACKAGE", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_release_that_needs_a_newer_Phoenix()
    {
        var result = ManifestValidator.Validate(
            Manifest(b => b.MinimumPhoenixVersion = "5.0.0"),
            Context());

        Assert.True(result.IsFailure);
        Assert.Equal("PX-MANIFEST-PHOENIX-TOO-OLD", result.Error.Code);
    }

    [Fact]
    public void A_prerelease_build_of_Phoenix_satisfies_its_own_release_version()
    {
        // 0.1.0-dev is "older" than 0.1.0 by semantic precedence, but for a minimum-tool-version
        // gate it counts as 0.1.0, otherwise a development build could never install anything.
        var context = Context() with { PhoenixVersion = SemanticVersion.Parse("0.1.0-dev") };
        var result = ManifestValidator.Validate(Manifest(b => b.MinimumPhoenixVersion = "0.1.0"), context);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Rejects_an_unsupported_schema_version()
    {
        var result = ManifestValidator.Validate(Manifest(b => b.SchemaVersion = 99), Context());

        Assert.True(result.IsFailure);
        Assert.Equal("PX-MANIFEST-SCHEMA", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_non_zip_package()
    {
        var result = ManifestValidator.Validate(Manifest(b => b.PackageFormat = "msi"), Context());

        Assert.True(result.IsFailure);
        Assert.Equal("PX-MANIFEST-FORMAT", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_health_endpoint_that_is_not_a_path()
    {
        var result = ManifestValidator.Validate(
            Manifest(b => b.HealthEndpoint = "https://somewhere-else.invalid/health"),
            Context());

        Assert.True(result.IsFailure);
        Assert.Equal("PX-MANIFEST-HEALTH", result.Error.Code);
    }

    [Fact]
    public void Rejects_a_null_manifest()
    {
        var result = ManifestValidator.Validate(null, Context());

        Assert.True(result.IsFailure);
        Assert.Equal("PX-MANIFEST-EMPTY", result.Error.Code);
    }

    [Fact]
    public void Round_trips_through_the_shared_serializer()
    {
        var json = ReleaseManifestSerializer.Serialize(Manifest());
        var parsed = ReleaseManifestSerializer.Deserialize(json);

        Assert.True(parsed.IsSuccess);
        Assert.Equal("phoenix-demoapp", parsed.Value.ApplicationId);
        Assert.Equal(ReleaseChannel.Production, parsed.Value.Channel);
        Assert.Single(parsed.Value.Packages);
    }

    [Fact]
    public void Reports_malformed_json_rather_than_throwing()
    {
        var parsed = ReleaseManifestSerializer.Deserialize("{ this is not json");

        Assert.True(parsed.IsFailure);
        Assert.Equal("PX-MANIFEST-JSON", parsed.Error.Code);
    }

    internal sealed class ManifestBuilder
    {
        public int SchemaVersion { get; set; } = 1;

        public string ApplicationId { get; set; } = "phoenix-demoapp";

        public string Version { get; set; } = "1.2.0";

        public ReleaseChannel Channel { get; set; } = ReleaseChannel.Production;

        public string Architecture { get; set; } = "x64";

        public string PackageFile { get; set; } = "phoenix-demoapp-win-x64.zip";

        public string PackageFormat { get; set; } = "zip";

        public string Sha256 { get; set; } = new('a', 64);

        public string Executable { get; set; } = "Phoenix.DemoApp.exe";

        public string? HealthEndpoint { get; set; } = "/health";

        public string? MinimumPhoenixVersion { get; set; }

        public ReleaseManifest Build() => new()
        {
            SchemaVersion = SchemaVersion,
            ApplicationId = ApplicationId,
            ApplicationName = "Demo",
            Version = Version,
            Channel = Channel,
            MinimumPhoenixVersion = MinimumPhoenixVersion,
            Packages =
            [
                new ManifestPackage
                {
                    OperatingSystem = "win",
                    Architecture = Architecture,
                    PackageFile = PackageFile,
                    PackageFormat = PackageFormat,
                    Sha256 = Sha256,
                    SizeBytes = 1024,
                    Executable = Executable,
                    Arguments = ["--urls", "http://localhost:5080"],
                    HealthEndpoint = HealthEndpoint,
                },
            ],
        };
    }
}
