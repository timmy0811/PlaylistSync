using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Models;

namespace PlaylistSync.Core.Storage;

/// <summary>
/// Loads and saves <c>settings.json</c> and <c>library.json</c> in the app config dir.
/// A missing file yields defaults. A corrupt file is renamed aside (never overwritten silently)
/// and defaults are returned, with a warning logged.
/// </summary>
public sealed class ConfigStore(AppPaths paths, ILogger<ConfigStore> log)
{
    public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) =>
        LoadAsync(paths.SettingsFile, () => new AppSettings(), ct);

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default) =>
        SaveAsync(paths.SettingsFile, settings, ct);

    public Task<Library> LoadLibraryAsync(CancellationToken ct = default) =>
        LoadAsync(paths.LibraryFile, () => new Library(), ct);

    public Task SaveLibraryAsync(Library library, CancellationToken ct = default) =>
        SaveAsync(paths.LibraryFile, library, ct);

    private async Task<T> LoadAsync<T>(string file, Func<T> defaults, CancellationToken ct) where T : class
    {
        if (!File.Exists(file)) return defaults();
        try
        {
            await using var fs = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<T>(fs, Json.Options, ct).ConfigureAwait(false) ?? defaults();
        }
        catch (JsonException ex)
        {
            var aside = AtomicFile.MoveAside(file);
            log.LogWarning(ex, "Config file {File} is corrupt; moved to {Aside} and using defaults", file, aside);
            return defaults();
        }
    }

    private static Task SaveAsync<T>(string file, T value, CancellationToken ct) =>
        AtomicFile.WriteAllTextAsync(file, JsonSerializer.Serialize(value, Json.Options), ct);
}
