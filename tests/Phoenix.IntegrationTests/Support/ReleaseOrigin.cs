using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Phoenix.Core.Models;
using Phoenix.Core.Releases;

namespace Phoenix.IntegrationTests.Support;

/// <summary>How one release should be built, including the ways it can be broken on purpose.</summary>
public sealed record ReleaseSpec
{
    public required string Version { get; init; }

    public ReleaseChannel Channel { get; init; } = ReleaseChannel.Production;

    public required int Port { get; init; }

    public string ApplicationId { get; init; } = "phoenix-demoapp";

    /// <summary>Passed to the demo application so it crashes ("crash") or reports unhealthy ("unhealthy").</summary>
    public string? Simulate { get; init; }

    /// <summary>Publishes a manifest whose SHA-256 does not match the package.</summary>
    public bool CorruptChecksum { get; init; }

    /// <summary>Publishes an archive containing a path-traversal entry.</summary>
    public bool MaliciousArchive { get; init; }

    /// <summary>Publishes a release with no manifest asset at all.</summary>
    public bool OmitManifest { get; init; }
}

/// <summary>
/// A directory of releases, laid out the way scripts/package.ps1 lays them out, served by
/// <see cref="FakeGitHubServer"/>. The payload is the real demo application build output, so
/// Phoenix installs and starts a genuine ASP.NET Core process.
/// </summary>
public sealed class ReleaseOrigin : IDisposable
{
    private readonly List<ReleaseSpec> _releases = [];

    public ReleaseOrigin()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "phoenix-tests", $"origin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public IReadOnlyList<ReleaseSpec> Releases => _releases;

    public void AddRelease(ReleaseSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var directory = Path.Combine(RootPath, spec.Version);
        Directory.CreateDirectory(directory);

        var packageName = "phoenix-demoapp-win-x64.zip";
        var packagePath = Path.Combine(directory, packageName);

        if (spec.MaliciousArchive)
        {
            CreateMaliciousArchive(packagePath);
        }
        else
        {
            CreateApplicationArchive(packagePath);
        }

        var sha = ComputeSha256(packagePath);
        if (spec.CorruptChecksum)
        {
            sha = new string('0', 64);
        }

        if (!spec.OmitManifest)
        {
            var environmentVariables = new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
            };

            if (spec.Simulate is not null)
            {
                environmentVariables["PHOENIX_DEMO_SIMULATE"] = spec.Simulate;
            }

            var manifest = new ReleaseManifest
            {
                ApplicationId = spec.ApplicationId,
                ApplicationName = "Phoenix Demo Application",
                Version = spec.Version,
                Channel = spec.Channel,
                Packages =
                [
                    new ManifestPackage
                    {
                        OperatingSystem = "win",
                        Architecture = "x64",
                        PackageFile = packageName,
                        PackageFormat = "zip",
                        Sha256 = sha,
                        SizeBytes = new FileInfo(packagePath).Length,
                        Executable = "Phoenix.DemoApp.exe",
                        Arguments = ["--urls", $"http://localhost:{spec.Port}"],
                        HealthEndpoint = "/health",
                        EnvironmentVariables = environmentVariables,
                    },
                ],
                Prerequisites = [new ManifestPrerequisite { Id = "aspnetcore-runtime", MinimumVersion = "9.0.0" }],
                MinimumPhoenixVersion = "0.1.0",
            };

            File.WriteAllText(
                Path.Combine(directory, ReleaseManifest.DefaultFileName),
                ReleaseManifestSerializer.Serialize(manifest));
        }

        _releases.Add(spec);
    }

    public string? ResolveAsset(string version, string file)
    {
        var candidate = Path.GetFullPath(Path.Combine(RootPath, version, file));

        return candidate.StartsWith(RootPath, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)
            ? candidate
            : null;
    }

    /// <summary>Builds the JSON shape the GitHub releases endpoint returns.</summary>
    public IReadOnlyList<object> BuildReleasePayload(Uri baseAddress)
    {
        var payload = new List<object>();

        foreach (var release in _releases.OrderByDescending(r => r.Version, StringComparer.Ordinal))
        {
            var directory = Path.Combine(RootPath, release.Version);
            var assets = new List<object>();

            foreach (var file in Directory.GetFiles(directory))
            {
                var name = Path.GetFileName(file);
                var url = new Uri(baseAddress, $"/assets/{release.Version}/{name}").ToString();

                assets.Add(new
                {
                    name,
                    size = new FileInfo(file).Length,
                    content_type = name.EndsWith(".zip", StringComparison.Ordinal)
                        ? "application/zip"
                        : "application/json",
                    browser_download_url = url,
                    url,
                });
            }

            payload.Add(new
            {
                tag_name = $"v{release.Version}",
                name = $"Demo {release.Version}",
                draft = false,
                prerelease = release.Version.Contains('-', StringComparison.Ordinal),
                published_at = DateTimeOffset.UtcNow.ToString("o"),
                assets,
            });
        }

        return payload;
    }

    private static void CreateApplicationArchive(string packagePath)
    {
        var source = DemoAppLocator.OutputDirectory;

        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

        using var stream = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);

            // Skip anything a real publish would not carry.
            if (relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            archive.CreateEntryFromFile(file, relative.Replace('\\', '/'), CompressionLevel.Fastest);
        }
    }

    private static void CreateMaliciousArchive(string packagePath)
    {
        using var stream = new FileStream(packagePath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var entry = archive.CreateEntry("../../pwned.txt");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write("this should never be written outside the staging directory");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best effort.
        }
    }
}

/// <summary>Finds the demo application's build output, which the tests package as a release.</summary>
internal static class DemoAppLocator
{
    public static string OutputDirectory { get; } = Resolve();

    private static string Resolve()
    {
        var configured = typeof(DemoAppLocator).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "DemoAppOutputDirectory")?.Value;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (File.Exists(Path.Combine(full, "Phoenix.DemoApp.exe")))
            {
                return full;
            }
        }

        throw new InvalidOperationException(
            "The demo application build output was not found. Build the solution before running the " +
            $"integration tests (looked in '{configured}').");
    }
}
