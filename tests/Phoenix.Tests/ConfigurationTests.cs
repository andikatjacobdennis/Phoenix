using Phoenix.Core.Configuration;
using Phoenix.Tests.TestSupport;

namespace Phoenix.Tests;

public class PhoenixOptionsValidatorTests
{
    private static PhoenixOptions Valid() => TestFactory.CreateOptions(@"C:\Temp\Phoenix");

    private static IReadOnlyList<string> Validate(PhoenixOptions options, string environment = "Production")
    {
        var result = new PhoenixOptionsValidator(environment).Validate(null, options);
        return result.Failures?.ToList() ?? [];
    }

    [Fact]
    public void Accepts_a_complete_configuration() =>
        Assert.Empty(Validate(Valid()));

    [Fact]
    public void Requires_the_identifying_fields()
    {
        var options = Valid();
        options.Company.Name = string.Empty;
        options.Application.Id = string.Empty;
        options.GitHub.Owner = string.Empty;

        var failures = Validate(options);

        Assert.Contains(failures, f => f.Contains("Company:Name", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Application:Id", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("GitHub:Owner", StringComparison.Ordinal));
    }

    [Fact]
    public void Requires_an_absolute_application_url()
    {
        var options = Valid();
        options.Application.Url = "localhost:5080";

        Assert.Contains(Validate(options), f => f.Contains("Application:Url", StringComparison.Ordinal));
    }

    [Fact]
    public void Requires_a_pinned_version_when_the_policy_is_pinned()
    {
        var options = Valid();
        options.Updates.Policy = UpdatePolicy.Pinned;

        Assert.Contains(Validate(options), f => f.Contains("PinnedVersion is required", StringComparison.Ordinal));

        options.Updates.PinnedVersion = "not-a-version";
        Assert.Contains(Validate(options), f => f.Contains("not a valid semantic version", StringComparison.Ordinal));
    }

    [Fact]
    public void Refuses_fault_injection_outside_development()
    {
        var options = Valid();
        options.Testing.SimulateFault = SimulatedFault.HealthCheckFailure;

        Assert.Contains(Validate(options, "Production"), f => f.Contains("only permitted in Development", StringComparison.Ordinal));
        Assert.Empty(Validate(options, "Development"));
    }

    [Fact]
    public void Refuses_simulated_prerequisite_installs_outside_development()
    {
        var options = Valid();
        options.Testing.SimulatePrerequisiteInstalls = true;

        Assert.Contains(Validate(options, "QA"), f => f.Contains("SimulatePrerequisiteInstalls", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_duplicate_prerequisite_ids()
    {
        var options = Valid();
        options.Prerequisites.Required.Add(new PrerequisiteDefinition
        {
            Id = "dotnet",
            DisplayName = ".NET",
            Detection = new DetectionOptions { Strategy = DetectionStrategy.DotNetRuntime, RuntimeName = "Microsoft.NETCore.App" },
        });
        options.Prerequisites.Required.Add(new PrerequisiteDefinition
        {
            Id = "dotnet",
            DisplayName = ".NET again",
            Detection = new DetectionOptions { Strategy = DetectionStrategy.DotNetRuntime, RuntimeName = "Microsoft.NETCore.App" },
        });

        Assert.Contains(Validate(options), f => f.Contains("defined more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void Requires_the_fields_each_detection_strategy_depends_on()
    {
        var options = Valid();
        options.Prerequisites.Required.Add(new PrerequisiteDefinition
        {
            Id = "runtime",
            DisplayName = "Runtime",
            Detection = new DetectionOptions { Strategy = DetectionStrategy.DotNetRuntime },
        });
        options.Prerequisites.Required.Add(new PrerequisiteDefinition
        {
            Id = "registry-thing",
            DisplayName = "Registry thing",
            Detection = new DetectionOptions { Strategy = DetectionStrategy.Registry },
        });

        var failures = Validate(options);

        Assert.Contains(failures, f => f.Contains("Detection:RuntimeName", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Detection:RegistryKey", StringComparison.Ordinal));
    }

    [Fact]
    public void Requires_https_and_a_checksum_for_a_downloaded_installer()
    {
        var options = Valid();
        options.Prerequisites.Required.Add(new PrerequisiteDefinition
        {
            Id = "python",
            DisplayName = "Python",
            Detection = new DetectionOptions
            {
                Strategy = DetectionStrategy.ExecutableVersion,
                Path = "python.exe",
            },
            Installer = new PrerequisiteInstallerOptions
            {
                Url = "http://insecure.invalid/python.exe",
                SilentArguments = "/quiet",
            },
        });

        var failures = Validate(options);

        Assert.Contains(failures, f => f.Contains("non-HTTPS", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Installer:Sha256", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_nonsensical_numbers()
    {
        var options = Valid();
        options.Network.RequestTimeoutSeconds = 0;
        options.HealthCheck.MaxAttempts = -1;
        options.Recovery.KeepVersions = 0;

        var failures = Validate(options);

        Assert.Contains(failures, f => f.Contains("RequestTimeoutSeconds", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("MaxAttempts", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("KeepVersions", StringComparison.Ordinal));
    }
}

public class PhoenixPathsTests
{
    [Fact]
    public void Expands_environment_variables_and_the_product_placeholder()
    {
        var options = TestFactory.CreateOptions(@"%TEMP%\Phoenix\{ProductSlug}");
        var paths = PhoenixPaths.Create(options, "Production");

        Assert.DoesNotContain("%TEMP%", paths.Root, StringComparison.Ordinal);
        Assert.DoesNotContain("{ProductSlug}", paths.Root, StringComparison.Ordinal);
        Assert.EndsWith("TestProduct", paths.Root, StringComparison.Ordinal);
    }

    [Fact]
    public void Derives_the_standard_layout_from_the_root()
    {
        using var temp = new TempDirectory("paths");
        var paths = PhoenixPaths.Create(TestFactory.CreateOptions(temp.Path), "Production");

        Assert.Equal(Path.Combine(paths.Root, "versions"), paths.Versions);
        Assert.Equal(Path.Combine(paths.Root, "staging"), paths.Staging);
        Assert.Equal(Path.Combine(paths.Root, "backup"), paths.Backup);
        Assert.Equal(Path.Combine(paths.Root, "cache"), paths.Cache);
        Assert.Equal(Path.Combine(paths.Root, "logs"), paths.Logs);
        Assert.Equal(Path.Combine(paths.Root, "state", "phoenix-state.json"), paths.StateFile);
    }

    [Fact]
    public void Honours_explicitly_configured_subdirectories()
    {
        using var temp = new TempDirectory("paths-custom");
        var options = TestFactory.CreateOptions(temp.Path);
        options.Directories.Logs = Path.Combine(temp.Path, "diagnostics");

        var paths = PhoenixPaths.Create(options, "Production");

        Assert.Equal(Path.Combine(temp.Path, "diagnostics"), paths.Logs);
    }

    [Theory]
    [InlineData("Example Application", "Example-Application")]
    [InlineData("Contoso.Widgets", "Contoso-Widgets")]
    [InlineData("!!!", "Application")]
    [InlineData("", "Application")]
    public void Builds_a_filesystem_safe_slug(string productName, string expected) =>
        Assert.Equal(expected, PhoenixPaths.ProductSlug(new ProductOptions { Name = productName }));

    [Fact]
    public void Creates_the_whole_tree()
    {
        using var temp = new TempDirectory("paths-create");
        var paths = PhoenixPaths.Create(TestFactory.CreateOptions(temp.Path), "Production");

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.Versions));
        Assert.True(Directory.Exists(paths.Backup));
        Assert.True(Directory.Exists(paths.StateDirectory));
    }
}
