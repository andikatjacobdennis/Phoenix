using System.Text.Json;
using System.Text.Json.Serialization;
using Phoenix.Core.Errors;
using Phoenix.Core.Models;

namespace Phoenix.Core.Releases;

/// <summary>
/// Reads and writes <c>release-manifest.json</c>. Shared by Phoenix, by the packaging
/// scripts and by the tests, so the format can never drift between them.
/// </summary>
public static class ReleaseManifestSerializer
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Result<ReleaseManifest> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return PhoenixError.Verification("PX-MANIFEST-EMPTY", "The release manifest was empty.");
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, Options);
            return manifest is null
                ? PhoenixError.Verification("PX-MANIFEST-NULL", "The release manifest deserialized to null.")
                : Result<ReleaseManifest>.Success(manifest);
        }
        catch (JsonException ex)
        {
            return PhoenixError.Verification(
                "PX-MANIFEST-JSON",
                $"The release manifest is not valid JSON: {ex.Message}",
                ex);
        }
    }

    public static string Serialize(ReleaseManifest manifest) =>
        JsonSerializer.Serialize(manifest, Options);
}
