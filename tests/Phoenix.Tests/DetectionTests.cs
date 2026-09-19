using Phoenix.Core.Configuration;
using Phoenix.Core.Models;
using Phoenix.Infrastructure.Platform;
using Phoenix.Infrastructure.Prerequisites.Detectors;
using Phoenix.Tests.TestSupport;

namespace Phoenix.Tests;

public class VersionExtractionTests
{
    [Theory]
    [InlineData("Python 3.12.4", "3.12.4")]
    [InlineData("v20.11.0", "20.11.0")]
    [InlineData("9.0.20", "9.0.20")]
    [InlineData("git version 2.54.0.windows.1", "2.54.0")]
    [InlineData("Microsoft.AspNetCore.App 10.0.12 [C:\\Program Files\\dotnet]", "10.0.12")]
    public void Reads_a_version_out_of_typical_tool_output(string output, string expected) =>
        Assert.Equal(expected, CommandVersionDetector.ExtractVersion(output, null)?.ToString());

    [Fact]
    public void Returns_null_when_there_is_no_version() =>
        Assert.Null(CommandVersionDetector.ExtractVersion("command not found", null));

    [Fact]
    public void Honours_a_custom_pattern()
    {
        var version = CommandVersionDetector.ExtractVersion(
            "Runtime build 7.4.1 (internal)",
            @"build (?<version>\d+\.\d+\.\d+)");

        Assert.Equal("7.4.1", version?.ToString());
    }

    [Theory]
    [InlineData("--version", new[] { "--version" })]
    [InlineData("-c \"import sys\"", new[] { "-c", "import sys" })]
    [InlineData("/quiet /norestart", new[] { "/quiet", "/norestart" })]
    [InlineData("", new string[0])]
    public void Splits_arguments_without_losing_quoted_values(string input, string[] expected) =>
        Assert.Equal(expected, CommandVersionDetector.SplitArguments(input));

    [Theory]
    [InlineData("9.0.20.12345", "9.0.20")]
    [InlineData("v14.38.33130", "14.38.33130")]
    [InlineData("3.1.0+abc123", "3.1.0")]
    [InlineData("1.2 (release)", "1.2")]
    public void Cleans_file_version_strings(string input, string expected) =>
        Assert.Equal(expected, FileVersionDetector.CleanVersion(input));
}

public class DotNetRuntimeDetectorTests
{
    [Fact]
    public async Task Finds_the_runtime_this_test_is_running_on()
    {
        var detector = new DotNetRuntimeDetector(TestFactory.Logger<DotNetRuntimeDetector>());

        var result = await detector.DetectAsync(
            new PrerequisiteDefinition
            {
                Id = "netcore",
                DisplayName = ".NET Runtime",
                Detection = new DetectionOptions
                {
                    Strategy = DetectionStrategy.DotNetRuntime,
                    RuntimeName = "Microsoft.NETCore.App",
                },
            },
            CancellationToken.None);

        Assert.True(result.IsInstalled);
        Assert.NotNull(result.DetectedVersion);
        Assert.NotEmpty(result.AllDetectedVersions);
    }

    [Fact]
    public async Task Reports_a_runtime_that_does_not_exist_as_missing()
    {
        var detector = new DotNetRuntimeDetector(TestFactory.Logger<DotNetRuntimeDetector>());

        var result = await detector.DetectAsync(
            new PrerequisiteDefinition
            {
                Id = "imaginary",
                DisplayName = "Imaginary Runtime",
                Detection = new DetectionOptions
                {
                    Strategy = DetectionStrategy.DotNetRuntime,
                    RuntimeName = "Contoso.Imaginary.App",
                },
            },
            CancellationToken.None);

        Assert.False(result.IsInstalled);
    }
}

public class SimulatedDetectorTests
{
    [Fact]
    public async Task AssumeMissing_always_reports_missing()
    {
        var result = await new AssumeMissingDetector().DetectAsync(
            new PrerequisiteDefinition { Id = "x", DisplayName = "X" },
            CancellationToken.None);

        Assert.False(result.IsInstalled);
    }

    [Fact]
    public async Task AssumePresent_reports_the_configured_minimum()
    {
        var result = await new AssumePresentDetector().DetectAsync(
            new PrerequisiteDefinition { Id = "x", DisplayName = "X", MinimumVersion = "4.2.0" },
            CancellationToken.None);

        Assert.True(result.IsInstalled);
        Assert.Equal("4.2.0", result.DetectedVersion?.ToString());
    }
}

public class ExecutableResolverTests
{
    [Fact]
    public void Finds_a_program_on_the_path()
    {
        // cmd.exe is on PATH on every Windows machine that can run these tests.
        var resolved = ExecutableResolver.Resolve("cmd");

        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));
        }
    }

    [Fact]
    public void Returns_null_for_something_that_is_not_installed() =>
        Assert.Null(ExecutableResolver.Resolve("definitely-not-a-real-program-xyz"));

    [Fact]
    public void Returns_null_rather_than_guessing_for_an_empty_value() =>
        Assert.Null(ExecutableResolver.Resolve("   "));

    [Fact]
    public void Expands_environment_variables()
    {
        using var temp = new TempDirectory("resolver");
        var file = temp.WriteFile("tool.exe", "x");

        Environment.SetEnvironmentVariable("PHOENIX_TEST_TOOL_DIR", temp.Path);
        try
        {
            Assert.Equal(file, ExecutableResolver.Resolve("%PHOENIX_TEST_TOOL_DIR%\\tool.exe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PHOENIX_TEST_TOOL_DIR", null);
        }
    }
}

public class UserDisplayNameTests
{
    [Theory]
    [InlineData("sarah.jones", "Sarah")]
    [InlineData("CONTOSO\\sarah.jones", "Sarah")]
    [InlineData("sarah.jones@contoso.com", "Sarah")]
    [InlineData("Jones, Sarah", "Sarah")]
    [InlineData("Sarah Jones", "Sarah")]
    [InlineData("admin", "Admin")]
    [InlineData("", "there")]
    public void Turns_an_account_name_into_a_greeting(string account, string expected) =>
        Assert.Equal(expected, EnvironmentService.Prettify(account));
}

public class PrerequisiteReportTests
{
    [Fact]
    public void A_reboot_requirement_is_visible_on_the_report()
    {
        var report = new PrerequisiteReport
        {
            Items =
            [
                new PrerequisiteStatus { Id = "a", DisplayName = "A", State = PrerequisiteState.Satisfied },
                new PrerequisiteStatus { Id = "b", DisplayName = "B", State = PrerequisiteState.RebootRequired },
            ],
        };

        Assert.True(report.RebootRequired);
        Assert.True(report.AnyInstalled);
        Assert.False(report.HasMandatoryFailure);
    }

    [Fact]
    public void An_optional_failure_does_not_block_the_run()
    {
        var report = new PrerequisiteReport
        {
            Items =
            [
                new PrerequisiteStatus
                {
                    Id = "optional",
                    DisplayName = "Optional",
                    State = PrerequisiteState.Failed,
                    IsMandatory = false,
                },
            ],
        };

        Assert.False(report.HasMandatoryFailure);
    }
}
