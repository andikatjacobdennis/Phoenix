using Phoenix.Core.Models;
using Phoenix.Infrastructure.Installation;
using Phoenix.Infrastructure.State;
using Phoenix.Tests.TestSupport;

namespace Phoenix.Tests;

public class CleanupServiceTests
{
    private static async Task<(CleanupService Service, Phoenix.Core.Configuration.PhoenixPaths Paths)> CreateAsync(
        TempDirectory temp,
        PhoenixState state,
        Action<Phoenix.Core.Configuration.PhoenixOptions>? configure = null)
    {
        var options = TestFactory.CreateOptions(temp.Path, configure);
        var paths = TestFactory.CreatePaths(options);
        var store = new JsonStateStore(paths, TestFactory.Logger<JsonStateStore>());
        await store.SaveAsync(state, CancellationToken.None);

        var service = new CleanupService(
            paths,
            TestFactory.Wrap(options),
            store,
            TestFactory.Logger<CleanupService>());

        return (service, paths);
    }

    [Fact]
    public async Task Never_removes_the_active_or_known_good_version()
    {
        using var temp = new TempDirectory("cleanup-protect");
        var state = PhoenixState.Empty with { InstalledVersion = "1.0.0", KnownGoodVersion = "0.9.0" };
        var (service, paths) = await CreateAsync(temp, state, o => o.Recovery.KeepVersions = 1);

        foreach (var version in new[] { "0.9.0", "1.0.0", "0.8.0", "0.7.0", "0.6.0" })
        {
            FakeInstallation.Create(paths, version);
        }

        await service.CleanupAsync(CancellationToken.None);

        Assert.True(Directory.Exists(paths.VersionDirectory("1.0.0")));
        Assert.True(Directory.Exists(paths.VersionDirectory("0.9.0")));

        // Only one unprotected version survives when KeepVersions is 1.
        var remaining = Directory.GetDirectories(paths.Versions).Select(Path.GetFileName).ToList();
        Assert.Equal(3, remaining.Count);
        Assert.Contains("0.8.0", remaining);
    }

    [Fact]
    public async Task Removes_abandoned_staging_directories()
    {
        using var temp = new TempDirectory("cleanup-staging");
        var (service, paths) = await CreateAsync(temp, PhoenixState.Empty);

        var stale = Path.Combine(paths.Staging, "PX-ABANDONED-1.0.0");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "payload.tmp"), "half a download");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));

        var recent = Path.Combine(paths.Staging, "PX-CURRENT-1.1.0");
        Directory.CreateDirectory(recent);

        await service.CleanupAsync(CancellationToken.None);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public async Task Removes_partial_downloads_and_expired_cache_entries()
    {
        using var temp = new TempDirectory("cleanup-cache");
        var state = PhoenixState.Empty with { InstalledVersion = "1.1.0", KnownGoodVersion = "1.1.0" };
        var (service, paths) = await CreateAsync(temp, state);

        var current = Path.Combine(paths.Cache, "1.1.0");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "package.zip"), "cached");

        var expired = Path.Combine(paths.Cache, "0.5.0");
        Directory.CreateDirectory(expired);
        File.WriteAllText(Path.Combine(expired, "package.zip"), "old");
        Directory.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-30));

        var partial = Path.Combine(current, "package.zip.part");
        File.WriteAllText(partial, "interrupted");

        await service.CleanupAsync(CancellationToken.None);

        Assert.True(Directory.Exists(current));
        Assert.False(Directory.Exists(expired));
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task Survives_an_empty_installation()
    {
        using var temp = new TempDirectory("cleanup-empty");
        var (service, _) = await CreateAsync(temp, PhoenixState.Empty);

        await service.CleanupAsync(CancellationToken.None);
    }
}

public class DirectoryOperationsTests
{
    [Fact]
    public void Copies_a_tree_including_nested_directories()
    {
        using var temp = new TempDirectory("dirops-copy");
        var source = temp.CreateSubdirectory("source");
        File.WriteAllText(Path.Combine(source, "root.txt"), "root");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "nested", "leaf.txt"), "leaf");

        var target = temp.Combine("target");
        DirectoryOperations.Copy(source, target);

        Assert.Equal("root", File.ReadAllText(Path.Combine(target, "root.txt")));
        Assert.Equal("leaf", File.ReadAllText(Path.Combine(target, "nested", "leaf.txt")));
    }

    [Fact]
    public void Deletes_read_only_files()
    {
        using var temp = new TempDirectory("dirops-readonly");
        var directory = temp.CreateSubdirectory("locked");
        var file = Path.Combine(directory, "readonly.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        Assert.True(DirectoryOperations.TryDelete(directory));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Deleting_something_that_is_not_there_succeeds()
    {
        using var temp = new TempDirectory("dirops-missing");
        Assert.True(DirectoryOperations.TryDelete(temp.Combine("never-existed")));
    }

    [Fact]
    public void Measures_the_size_of_a_tree()
    {
        using var temp = new TempDirectory("dirops-size");
        var directory = temp.CreateSubdirectory("measured");
        File.WriteAllText(Path.Combine(directory, "a.txt"), new string('x', 100));
        File.WriteAllText(Path.Combine(directory, "b.txt"), new string('x', 50));

        Assert.Equal(150, DirectoryOperations.MeasureSize(directory));
    }
}
