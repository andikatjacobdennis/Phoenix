using Phoenix.Core.Abstractions;
using Phoenix.Core.Models;
using Phoenix.Core.Versioning;
using Phoenix.Infrastructure.Installation;
using Phoenix.Infrastructure.State;
using Phoenix.Tests.TestSupport;

namespace Phoenix.Tests;

public class JsonStateStoreTests
{
    [Fact]
    public async Task Round_trips_state()
    {
        using var temp = new TempDirectory("state-roundtrip");
        var paths = TestFactory.CreatePaths(TestFactory.CreateOptions(temp.Path));

        var store = new JsonStateStore(paths, TestFactory.Logger<JsonStateStore>());
        await store.SaveAsync(
            PhoenixState.Empty with { InstalledVersion = "1.2.0", KnownGoodVersion = "1.1.0" },
            CancellationToken.None);

        var reloaded = await new JsonStateStore(paths, TestFactory.Logger<JsonStateStore>())
            .LoadAsync(CancellationToken.None);

        Assert.Equal("1.2.0", reloaded.InstalledVersion);
        Assert.Equal("1.1.0", reloaded.KnownGoodVersion);
    }

    [Fact]
    public async Task A_first_run_is_not_treated_as_a_recovery()
    {
        using var temp = new TempDirectory("state-first-run");
        var paths = TestFactory.CreatePaths(TestFactory.CreateOptions(temp.Path));
        var store = new JsonStateStore(paths, TestFactory.Logger<JsonStateStore>());

        var state = await store.LoadAsync(CancellationToken.None);

        Assert.Null(state.InstalledVersion);
        Assert.False(store.LastLoadWasRecovered);
    }

    [Fact]
    public async Task A_corrupt_state_file_is_reported_rather_than_thrown()
    {
        using var temp = new TempDirectory("state-corrupt");
        var paths = TestFactory.CreatePaths(TestFactory.CreateOptions(temp.Path));
        await File.WriteAllTextAsync(paths.StateFile, "{ not json at all");

        var store = new JsonStateStore(paths, TestFactory.Logger<JsonStateStore>());
        var state = await store.LoadAsync(CancellationToken.None);

        Assert.True(store.LastLoadWasRecovered);
        Assert.Null(state.InstalledVersion);
    }

    [Fact]
    public async Task Falls_back_to_the_backup_when_the_main_file_is_damaged()
    {
        using var temp = new TempDirectory("state-backup");
        var paths = TestFactory.CreatePaths(TestFactory.CreateOptions(temp.Path));
        var store = new JsonStateStore(paths, TestFactory.Logger<JsonStateStore>());

        // Two writes: the second leaves the first behind as .bak.
        await store.SaveAsync(PhoenixState.Empty with { InstalledVersion = "1.0.0" }, CancellationToken.None);
        await store.SaveAsync(PhoenixState.Empty with { InstalledVersion = "1.1.0" }, CancellationToken.None);

        Assert.True(File.Exists(paths.StateFile + ".bak"));

        await File.WriteAllTextAsync(paths.StateFile, "corrupted");

        var recovered = await new JsonStateStore(paths, TestFactory.Logger<JsonStateStore>())
            .LoadAsync(CancellationToken.None);

        Assert.Equal("1.0.0", recovered.InstalledVersion);
    }

    [Fact]
    public async Task Update_applies_a_change_and_persists_it()
    {
        using var temp = new TempDirectory("state-update");
        var paths = TestFactory.CreatePaths(TestFactory.CreateOptions(temp.Path));
        var store = new JsonStateStore(paths, TestFactory.Logger<JsonStateStore>());

        await store.UpdateAsync(s => s with { InstalledVersion = "2.0.0" }, CancellationToken.None);
        var updated = await store.UpdateAsync(s => s.WithRejected("2.1.0"), CancellationToken.None);

        Assert.Equal("2.0.0", updated.InstalledVersion);
        Assert.Contains("2.1.0", updated.RejectedVersions);
    }

    [Fact]
    public void Rejected_versions_are_deduplicated_and_bounded()
    {
        var state = PhoenixState.Empty;

        for (var i = 0; i < 40; i++)
        {
            state = state.WithRejected($"1.0.{i}");
        }

        state = state.WithRejected("1.0.39");

        Assert.Equal(20, state.RejectedVersions.Count);
        Assert.Contains("1.0.39", state.RejectedVersions);
    }
}

public class InstallationManagerTests
{
    private static (InstallationManager Manager, IStateStore Store, Phoenix.Core.Configuration.PhoenixPaths Paths)
        Create(TempDirectory temp)
    {
        var options = TestFactory.CreateOptions(temp.Path);
        var paths = TestFactory.CreatePaths(options);
        var store = new JsonStateStore(paths, TestFactory.Logger<JsonStateStore>());

        var manager = new InstallationManager(
            paths,
            TestFactory.Wrap(options),
            store,
            TestFactory.CreateFaultInjector(options),
            TestFactory.Logger<InstallationManager>());

        return (manager, store, paths);
    }

    [Fact]
    public void Lists_only_usable_version_directories()
    {
        using var temp = new TempDirectory("install-list");
        var (manager, _, paths) = Create(temp);

        FakeInstallation.Create(paths, "1.0.0");
        FakeInstallation.Create(paths, "1.1.0");
        FakeInstallation.Create(paths, "1.2.0", includeExecutable: false);
        FakeInstallation.Create(paths, "1.3.0", includeDescriptor: false);
        Directory.CreateDirectory(paths.VersionDirectory("not-a-version"));

        var versions = manager.ListInstalledVersions().Select(i => i.Version.ToString()).ToArray();

        Assert.Equal(["1.1.0", "1.0.0"], versions);
    }

    [Fact]
    public async Task Activating_records_the_version_in_state()
    {
        using var temp = new TempDirectory("install-activate");
        var (manager, store, paths) = Create(temp);
        FakeInstallation.Create(paths, "1.0.0");

        var installation = manager.ListInstalledVersions().Single();
        var result = await manager.ActivateAsync(installation, CancellationToken.None);

        Assert.True(result.IsSuccess);

        var state = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("1.0.0", state.InstalledVersion);
        Assert.Null(state.KnownGoodVersion);
    }

    [Fact]
    public async Task Marking_known_good_creates_an_independent_backup()
    {
        using var temp = new TempDirectory("install-knowngood");
        var (manager, store, paths) = Create(temp);
        FakeInstallation.Create(paths, "1.0.0");

        var installation = manager.ListInstalledVersions().Single();
        await manager.ActivateAsync(installation, CancellationToken.None);
        await manager.MarkKnownGoodAsync(installation, CancellationToken.None);

        var state = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("1.0.0", state.KnownGoodVersion);
        Assert.NotNull(state.LastSuccessfulUpdate);
        Assert.True(Directory.Exists(paths.BackupDirectory("1.0.0")));
    }

    [Fact]
    public async Task Rolls_back_to_the_known_good_version()
    {
        using var temp = new TempDirectory("install-rollback");
        var (manager, store, paths) = Create(temp);
        FakeInstallation.Create(paths, "1.0.0");
        FakeInstallation.Create(paths, "1.1.0");

        var good = manager.ListInstalledVersions().Single(i => i.Version.ToString() == "1.0.0");
        await manager.ActivateAsync(good, CancellationToken.None);
        await manager.MarkKnownGoodAsync(good, CancellationToken.None);

        var broken = manager.ListInstalledVersions().Single(i => i.Version.ToString() == "1.1.0");
        await manager.ActivateAsync(broken, CancellationToken.None);

        var rollback = await manager.RollbackAsync(
            SemanticVersion.Parse("1.1.0"),
            "health check failed",
            CancellationToken.None);

        Assert.True(rollback.IsSuccess);
        Assert.Equal("1.0.0", rollback.Value.Version.ToString());

        var state = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("1.0.0", state.InstalledVersion);
        Assert.Contains("1.1.0", state.RejectedVersions);
        Assert.Equal("1.1.0", state.Recovery!.FailedVersion);
    }

    [Fact]
    public async Task Restores_the_known_good_version_from_backup_when_its_directory_is_gone()
    {
        using var temp = new TempDirectory("install-restore-backup");
        var (manager, _, paths) = Create(temp);
        FakeInstallation.Create(paths, "1.0.0");

        var good = manager.ListInstalledVersions().Single();
        await manager.ActivateAsync(good, CancellationToken.None);
        await manager.MarkKnownGoodAsync(good, CancellationToken.None);

        // Simulate the active directory being destroyed between runs.
        Directory.Delete(paths.VersionDirectory("1.0.0"), recursive: true);
        Assert.False(Directory.Exists(paths.VersionDirectory("1.0.0")));

        var rollback = await manager.RollbackAsync(null, "directory lost", CancellationToken.None);

        Assert.True(rollback.IsSuccess);
        Assert.Equal("1.0.0", rollback.Value.Version.ToString());
        Assert.True(File.Exists(rollback.Value.ExecutablePath));
    }

    [Fact]
    public async Task Rollback_fails_clearly_when_there_is_nothing_to_restore()
    {
        using var temp = new TempDirectory("install-no-target");
        var (manager, _, _) = Create(temp);

        var rollback = await manager.RollbackAsync(
            SemanticVersion.Parse("1.0.0"),
            "nothing installed",
            CancellationToken.None);

        Assert.True(rollback.IsFailure);
        Assert.Equal("PX-RECOVERY-NO-TARGET", rollback.Error.Code);
        Assert.Equal(Phoenix.Core.Errors.ExitCode.RecoveryFailure, rollback.Error.ExitCode);
    }

    [Fact]
    public async Task Rebuilds_state_from_the_filesystem()
    {
        using var temp = new TempDirectory("install-repair");
        var (manager, _, paths) = Create(temp);
        FakeInstallation.Create(paths, "1.0.0");
        FakeInstallation.Create(paths, "1.2.0");

        // A backup is the only surviving evidence that 1.0.0 once worked.
        Directory.CreateDirectory(paths.Backup);
        DirectoryCopy(paths.VersionDirectory("1.0.0"), paths.BackupDirectory("1.0.0"));

        var repaired = await manager.RepairStateAsync(CancellationToken.None);

        Assert.Equal("1.2.0", repaired.InstalledVersion);
        Assert.Equal("1.0.0", repaired.KnownGoodVersion);
    }

    [Fact]
    public async Task The_active_installation_falls_back_when_state_points_at_a_missing_version()
    {
        using var temp = new TempDirectory("install-stale-state");
        var (manager, store, paths) = Create(temp);
        FakeInstallation.Create(paths, "1.0.0");

        await store.SaveAsync(
            PhoenixState.Empty with { InstalledVersion = "9.9.9", KnownGoodVersion = "1.0.0" },
            CancellationToken.None);

        var active = await manager.GetActiveInstallationAsync(CancellationToken.None);

        Assert.NotNull(active);
        Assert.Equal("1.0.0", active.Version.ToString());
        Assert.True(active.IsKnownGood);
    }

    private static void DirectoryCopy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }
}
