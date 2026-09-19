using System.Net;
using Phoenix.Core.Errors;
using Phoenix.Core.Models;
using Phoenix.IntegrationTests.Support;

namespace Phoenix.IntegrationTests;

/// <summary>
/// The scenarios that matter: a first installation, an update, a broken release, and every way
/// the machine is supposed to end up with a working application anyway.
///
/// Nothing here is mocked past the release source. Phoenix downloads over HTTP, verifies real
/// checksums, extracts real archives, starts a real ASP.NET Core process and polls its real
/// health endpoint.
/// </summary>
public sealed class EndToEndTests
{
    private const string Owner = "test-owner";
    private const string Repository = "test-repo";

    private static async Task<HttpStatusCode?> ProbeHealthAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        try
        {
            using var response = await client.GetAsync(new Uri($"http://localhost:{port}/health"));
            return response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }


    private static void AssertExit(ExitCode expected, ExitCode actual, PhoenixHarness machine)
    {
        Assert.True(
            expected == actual,
            $"Expected exit code {expected} but got {actual}.{Environment.NewLine}{machine.LastUi.Describe()}");
    }

    [Fact]
    public async Task First_run_installs_verifies_starts_and_health_checks_the_application()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        var exitCode = await machine.RunAsync();

        AssertExit(ExitCode.Success, exitCode, machine);

        var state = await machine.ReadStateAsync();
        Assert.Equal("1.0.0", state.InstalledVersion);
        Assert.Equal("1.0.0", state.KnownGoodVersion);
        Assert.NotNull(state.LastSuccessfulUpdate);

        // A known-good version is backed up independently of its version directory.
        Assert.True(Directory.Exists(machine.Paths.BackupDirectory("1.0.0")));

        // The application really is serving.
        Assert.Equal(HttpStatusCode.OK, await ProbeHealthAsync(port));

        Assert.True(machine.LastUi.ReadyShown);
        Assert.Empty(machine.LastUi.Failures);

        // The prerequisite phase ran against the real machine.
        Assert.NotNull(machine.LastUi.LastPrerequisiteReport);
        Assert.Contains(
            machine.LastUi.LastPrerequisiteReport!.Items,
            i => i.Id == "aspnetcore-runtime" && i.State == PrerequisiteState.Satisfied);
    }

    [Fact]
    public async Task A_newer_release_is_installed_and_becomes_the_known_good_version()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        AssertExit(ExitCode.Success, await machine.RunAsync(), machine);

        // A new release appears.
        origin.AddRelease(new ReleaseSpec { Version = "1.1.0", Port = port });

        AssertExit(ExitCode.Success, await machine.RunAsync(), machine);

        var state = await machine.ReadStateAsync();
        Assert.Equal("1.1.0", state.InstalledVersion);
        Assert.Equal("1.1.0", state.KnownGoodVersion);

        // The previous version is kept on disk, which is what makes rollback instant.
        Assert.Contains("1.0.0", machine.InstalledVersions());

        Assert.Equal(HttpStatusCode.OK, await ProbeHealthAsync(port));

        var announcement = machine.LastUi.UpdateAnnouncement;
        Assert.NotNull(announcement);
        Assert.Equal("1.0.0", announcement!.Value.Current?.ToString());
        Assert.Equal("1.1.0", announcement.Value.Next.ToString());
    }

    [Fact]
    public async Task A_release_that_never_becomes_healthy_is_rolled_back()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        // No remote fallback: this test is about the local known-good copy.
        machine.With("Phoenix:Updates:RemoteFallbackCount", "0")
               .With("Phoenix:HealthCheck:MaxAttempts", "4")
               .With("Phoenix:HealthCheck:OverallTimeoutSeconds", "8");

        AssertExit(ExitCode.Success, await machine.RunAsync(), machine);

        // 1.1.0 starts, but reports unhealthy for ever.
        origin.AddRelease(new ReleaseSpec { Version = "1.1.0", Port = port, Simulate = "unhealthy" });

        var exitCode = await machine.RunAsync();

        AssertExit(ExitCode.InstallationFailure, exitCode, machine);

        var state = await machine.ReadStateAsync();
        Assert.Equal("1.0.0", state.InstalledVersion);
        Assert.Equal("1.0.0", state.KnownGoodVersion);
        Assert.Contains("1.1.0", state.RejectedVersions);
        Assert.Equal("1.1.0", state.Recovery?.FailedVersion);

        // The user was told what happened, and the application is usable again.
        Assert.NotEmpty(machine.LastUi.RecoveryNotes);
        Assert.Equal(HttpStatusCode.OK, await ProbeHealthAsync(port));
    }

    [Fact]
    public async Task A_release_that_crashes_on_startup_is_rolled_back()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);
        machine.With("Phoenix:Updates:RemoteFallbackCount", "0");

        AssertExit(ExitCode.Success, await machine.RunAsync(), machine);

        origin.AddRelease(new ReleaseSpec { Version = "1.2.0", Port = port, Simulate = "crash" });

        var exitCode = await machine.RunAsync();

        AssertExit(ExitCode.InstallationFailure, exitCode, machine);

        var state = await machine.ReadStateAsync();
        Assert.Equal("1.0.0", state.InstalledVersion);
        Assert.Contains("1.2.0", state.RejectedVersions);
        Assert.Equal(HttpStatusCode.OK, await ProbeHealthAsync(port));
    }

    [Fact]
    public async Task A_package_whose_checksum_does_not_match_is_refused_and_an_older_release_is_used()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });
        origin.AddRelease(new ReleaseSpec { Version = "1.1.0", Port = port, CorruptChecksum = true });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        var exitCode = await machine.RunAsync();

        // Remote fallback: the newest release is unusable, the previous one is installed.
        AssertExit(ExitCode.Success, exitCode, machine);

        var state = await machine.ReadStateAsync();
        Assert.Equal("1.0.0", state.InstalledVersion);
        Assert.Equal("1.0.0", state.KnownGoodVersion);
        Assert.Contains("1.1.0", state.RejectedVersions);

        // The rejected version was never installed.
        Assert.DoesNotContain("1.1.0", machine.InstalledVersions());
        Assert.Equal(HttpStatusCode.OK, await ProbeHealthAsync(port));
    }

    [Fact]
    public async Task An_archive_that_tries_to_escape_the_staging_directory_is_refused()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port, MaliciousArchive = true });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        var exitCode = await machine.RunAsync();

        AssertExit(ExitCode.InstallationFailure, exitCode, machine);
        Assert.Empty(machine.InstalledVersions());

        // Nothing was written outside the staging directory.
        Assert.False(File.Exists(Path.Combine(machine.RootPath, "pwned.txt")));
        Assert.False(File.Exists(Path.Combine(machine.RootPath, "..", "pwned.txt")));

        var failure = Assert.Single(machine.LastUi.Failures);
        Assert.True(failure.Category is ErrorCategory.Security or ErrorCategory.Installation);
    }

    [Fact]
    public async Task A_release_belonging_to_another_application_is_refused()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec
        {
            Version = "1.0.0",
            Port = port,
            ApplicationId = "somebody-elses-application",
        });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        var exitCode = await machine.RunAsync();

        AssertExit(ExitCode.InstallationFailure, exitCode, machine);
        Assert.Empty(machine.InstalledVersions());
    }

    [Fact]
    public async Task A_production_machine_ignores_a_qa_release()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec
        {
            Version = "2.0.0-qa.1",
            Port = port,
            Channel = ReleaseChannel.QA,
        });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        var exitCode = await machine.RunAsync();

        // Nothing installable for this channel, and nothing installed locally.
        AssertExit(ExitCode.InstallationFailure, exitCode, machine);
        Assert.Empty(machine.InstalledVersions());
    }

    [Fact]
    public async Task A_qa_machine_installs_the_qa_release()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });
        origin.AddRelease(new ReleaseSpec
        {
            Version = "2.0.0-qa.1",
            Port = port,
            Channel = ReleaseChannel.QA,
        });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);
        machine.With("Phoenix:Updates:Channel", "QA");

        AssertExit(ExitCode.Success, await machine.RunAsync(), machine);

        var state = await machine.ReadStateAsync();
        Assert.Equal("2.0.0-qa.1", state.InstalledVersion);
    }

    [Fact]
    public async Task When_the_release_source_is_unreachable_the_installed_version_is_launched()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        AssertExit(ExitCode.Success, await machine.RunAsync(), machine);
        machine.StopApplications();

        // GitHub is now down.
        server.ForcedStatusCode = HttpStatusCode.ServiceUnavailable;

        var exitCode = await machine.RunAsync();

        AssertExit(ExitCode.Success, exitCode, machine);
        Assert.Equal(HttpStatusCode.OK, await ProbeHealthAsync(port));

        var state = await machine.ReadStateAsync();
        Assert.Equal("1.0.0", state.InstalledVersion);
    }

    [Fact]
    public async Task With_nothing_installed_and_no_release_source_Phoenix_fails_cleanly()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        server.ForcedStatusCode = HttpStatusCode.ServiceUnavailable;

        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        var exitCode = await machine.RunAsync();

        AssertExit(ExitCode.InstallationFailure, exitCode, machine);
        Assert.Empty(machine.InstalledVersions());

        var failure = Assert.Single(machine.LastUi.Failures);

        // The user gets a sentence, not an exception.
        Assert.DoesNotContain("Exception", failure.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", failure.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_up_to_date_machine_launches_without_downloading_anything()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        AssertExit(ExitCode.Success, await machine.RunAsync(), machine);
        machine.StopApplications();

        var downloadsAfterFirstRun = server.AssetRequests;

        AssertExit(ExitCode.Success, await machine.RunAsync(), machine);

        Assert.Equal(downloadsAfterFirstRun, server.AssetRequests);
        Assert.Null(machine.LastUi.UpdateAnnouncement);
        Assert.Equal(HttpStatusCode.OK, await ProbeHealthAsync(port));
    }

    [Fact]
    public async Task Rollback_can_be_requested_explicitly()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        AssertExit(ExitCode.Success, await machine.RunAsync(), machine);

        origin.AddRelease(new ReleaseSpec { Version = "1.1.0", Port = port });
        AssertExit(ExitCode.Success, await machine.RunAsync(), machine);

        machine.StopApplications();

        var exitCode = await machine.RunAsync(new Core.Orchestration.PhoenixRunOptions
        {
            OperationId = "PX-TEST01",
            Rollback = true,
        });

        AssertExit(ExitCode.Success, exitCode, machine);

        var state = await machine.ReadStateAsync();

        // 1.1.0 was known good, so an explicit rollback restores it and it keeps running.
        Assert.Equal("1.1.0", state.InstalledVersion);
        Assert.Equal(HttpStatusCode.OK, await ProbeHealthAsync(port));
    }

    [Fact]
    public async Task A_second_Phoenix_instance_is_refused_rather_than_racing_the_first()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        // A Windows mutex is owned by a thread, and a thread that already owns one can take it
        // again. To stand in for a second Phoenix process, the lock is held on its own thread.
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var acquiredSuccessfully = false;

        var holder = new Thread(() =>
        {
            var singleInstance = new Phoenix.Infrastructure.Platform.SingleInstanceManager(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    Phoenix.Infrastructure.Platform.SingleInstanceManager>.Instance);

            var held = singleInstance.TryAcquire(machine.Paths.Root);
            acquiredSuccessfully = held.IsSuccess;
            acquired.Set();

            release.Wait();

            if (held.IsSuccess)
            {
                held.Value.Dispose();
            }
        })
        {
            IsBackground = true,
        };

        holder.Start();
        acquired.Wait(TimeSpan.FromSeconds(5));
        Assert.True(acquiredSuccessfully);

        try
        {
            var exitCode = await machine.RunAsync();
            AssertExit(ExitCode.AlreadyRunning, exitCode, machine);
            Assert.Empty(machine.InstalledVersions());
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Check_only_reports_an_update_without_installing_it()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        AssertExit(ExitCode.Success, await machine.RunAsync(), machine);
        machine.StopApplications();

        origin.AddRelease(new ReleaseSpec { Version = "1.1.0", Port = port });

        var exitCode = await machine.RunAsync(new Core.Orchestration.PhoenixRunOptions
        {
            OperationId = "PX-TEST02",
            CheckOnly = true,
        });

        AssertExit(ExitCode.Success, exitCode, machine);
        Assert.DoesNotContain("1.1.0", machine.InstalledVersions());

        var state = await machine.ReadStateAsync();
        Assert.Equal("1.0.0", state.InstalledVersion);
    }

    [Fact]
    public async Task Cancelling_mid_run_leaves_nothing_half_installed()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exitCode = await machine.RunAsync(cancellationToken: cancellation.Token);

        AssertExit(ExitCode.Cancelled, exitCode, machine);
        Assert.Empty(machine.InstalledVersions());

        var state = await machine.ReadStateAsync();
        Assert.Null(state.InstalledVersion);
    }

    [Fact]
    public async Task Corrupt_state_is_rebuilt_from_what_is_on_disk()
    {
        var port = PhoenixHarness.GetFreePort();

        using var origin = new ReleaseOrigin();
        origin.AddRelease(new ReleaseSpec { Version = "1.0.0", Port = port });

        await using var server = await FakeGitHubServer.StartAsync(origin, Owner, Repository);
        await using var machine = new PhoenixHarness(server.BaseAddress, port);

        AssertExit(ExitCode.Success, await machine.RunAsync(), machine);
        machine.StopApplications();

        // Something shredded the state file, and its backup, between runs.
        await File.WriteAllTextAsync(machine.Paths.StateFile, "{{{ not json");
        File.Delete(machine.Paths.StateFile + ".bak");

        server.ForcedStatusCode = HttpStatusCode.ServiceUnavailable;

        var exitCode = await machine.RunAsync();

        AssertExit(ExitCode.Success, exitCode, machine);

        var state = await machine.ReadStateAsync();
        Assert.Equal("1.0.0", state.InstalledVersion);
        Assert.Equal(HttpStatusCode.OK, await ProbeHealthAsync(port));
    }
}
