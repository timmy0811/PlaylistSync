using System.Text.Json;
using PlaylistSync.Core.Models;

namespace PlaylistSync.Core.Storage;

/// <summary>
/// Reads and writes the per-directory manifest <c>.playlist-sync.json</c>.
/// Unlike config files, a corrupt manifest is never replaced: it holds the track history,
/// so loading throws <see cref="StorageException"/> and the user decides what to do.
/// </summary>
public sealed class ManifestStore
{
    public const string FileName = ".playlist-sync.json";

    public static string PathFor(string directory) => Path.Combine(directory, FileName);

    /// <summary>Returns the manifest, or null if the directory has none yet.</summary>
    public async Task<Manifest?> LoadAsync(string directory, CancellationToken ct = default)
    {
        var file = PathFor(directory);
        if (!File.Exists(file)) return null;

        Manifest? manifest;
        try
        {
            await using var fs = File.OpenRead(file);
            manifest = await JsonSerializer.DeserializeAsync<Manifest>(fs, Json.Options, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new StorageException($"The manifest '{file}' is corrupt: {ex.Message}", ex);
        }

        if (manifest is null)
            throw new StorageException($"The manifest '{file}' is empty.");
        if (manifest.Version > Manifest.CurrentVersion)
            throw new StorageException(
                $"The manifest '{file}' was written by a newer version of Playlist Sync (format {manifest.Version}).");

        // Comparer is lost on deserialization; re-wrap so lookups stay ordinal.
        manifest.Tracks = new Dictionary<string, TrackRecord>(manifest.Tracks, StringComparer.Ordinal);
        return manifest;
    }

    /// <summary>
    /// Saves atomically. Writes are serialized so parallel downloads finishing at the same time
    /// can't race on the temp file. The caller must not mutate <paramref name="manifest"/>
    /// concurrently with this call (the sync service holds its own lock while snapshotting).
    /// </summary>
    public async Task SaveAsync(string directory, Manifest manifest, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(manifest, Json.Options);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteAllTextAsync(PathFor(directory), json, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private readonly SemaphoreSlim _writeLock = new(1, 1);
}
