using System.Text.Json;
using Phoenix.Core.Configuration;

namespace Phoenix.Tests.TestSupport;

/// <summary>
/// Creates a version directory that looks exactly like one Phoenix produced: a descriptor
/// plus the executable it names.
/// </summary>
public static class FakeInstallation
{
    public static string Create(
        PhoenixPaths paths,
        string version,
        string executable = "App.exe",
        bool includeExecutable = true,
        bool includeDescriptor = true)
    {
        var directory = paths.VersionDirectory(version);
        Directory.CreateDirectory(directory);

        if (includeExecutable)
        {
            File.WriteAllText(Path.Combine(directory, executable), "fake executable");
        }

        if (includeDescriptor)
        {
            var descriptor = new
            {
                schemaVersion = 1,
                applicationId = "phoenix-demoapp",
                version,
                channel = "Production",
                executable,
                arguments = new[] { "--urls", "http://localhost:5080" },
                healthEndpoint = "/health",
                environmentVariables = new Dictionary<string, string>(),
                sourceTag = $"v{version}",
                installedAt = DateTimeOffset.UtcNow,
            };

            File.WriteAllText(
                Path.Combine(directory, ".phoenix-install.json"),
                JsonSerializer.Serialize(descriptor, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        return directory;
    }
}
