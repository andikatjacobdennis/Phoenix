using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Models;

namespace Phoenix.Infrastructure.State;

/// <summary>
/// Stores Phoenix state as JSON, written atomically.
///
/// The write sequence is: serialise to <c>.tmp</c>, flush to disk, keep the previous file as
/// <c>.bak</c>, then replace. A power cut can therefore lose the newest state, but can never
/// leave a half-written file that makes recovery impossible - and if it somehow does, the
/// backup is tried, and after that the installation manager rebuilds state from the filesystem.
/// </summary>
public sealed class JsonStateStore : IStateStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly PhoenixPaths _paths;
    private readonly ILogger<JsonStateStore> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private PhoenixState? _cached;

    public JsonStateStore(PhoenixPaths paths, ILogger<JsonStateStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public bool LastLoadWasRecovered { get; private set; }

    public async Task<PhoenixState> LoadAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            _cached = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<PhoenixState> ReadAsync(CancellationToken cancellationToken)
    {
        var primary = await TryReadFileAsync(_paths.StateFile, cancellationToken).ConfigureAwait(false);
        if (primary is not null)
        {
            LastLoadWasRecovered = false;
            return primary;
        }

        if (!File.Exists(_paths.StateFile))
        {
            // Genuinely the first run: nothing to recover.
            LastLoadWasRecovered = false;
            return PhoenixState.Empty;
        }

        _logger.LogWarning("State file {Path} was unreadable; trying the backup.", _paths.StateFile);

        var backup = await TryReadFileAsync(_paths.StateFile + ".bak", cancellationToken).ConfigureAwait(false);
        LastLoadWasRecovered = true;
        return backup ?? PhoenixState.Empty;
    }

    private async Task<PhoenixState?> TryReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            var state = await JsonSerializer
                .DeserializeAsync<PhoenixState>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (state is null)
            {
                return null;
            }

            if (state.SchemaVersion > PhoenixState.CurrentSchemaVersion)
            {
                _logger.LogWarning(
                    "State file schema version {Found} is newer than this Phoenix understands ({Supported}).",
                    state.SchemaVersion,
                    PhoenixState.CurrentSchemaVersion);
                return null;
            }

            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read state from {Path}.", path);
            return null;
        }
    }

    public async Task SaveAsync(PhoenixState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicallyAsync(state, cancellationToken).ConfigureAwait(false);
            _cached = state;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<PhoenixState> UpdateAsync(
        Func<PhoenixState, PhoenixState> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached ??= await ReadAsync(cancellationToken).ConfigureAwait(false);
            var updated = change(_cached);
            await WriteAtomicallyAsync(updated, cancellationToken).ConfigureAwait(false);
            _cached = updated;
            return updated;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task WriteAtomicallyAsync(PhoenixState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.StateDirectory);

        var temporary = _paths.StateFile + ".tmp";
        var backup = _paths.StateFile + ".bak";

        await using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            // Make sure the bytes are on the device before anything is replaced.
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_paths.StateFile))
        {
            File.Replace(temporary, _paths.StateFile, backup, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporary, _paths.StateFile);
        }

        _logger.LogDebug(
            "State saved: installed={Installed}, knownGood={KnownGood}.",
            state.InstalledVersion,
            state.KnownGoodVersion);
    }

    public void Dispose() => _mutex.Dispose();
}
