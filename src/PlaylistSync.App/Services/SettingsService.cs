using PlaylistSync.Core.Models;
using PlaylistSync.Core.Storage;

namespace PlaylistSync.App.Services;

/// <summary>Holds the current <see cref="AppSettings"/> for the UI. Loaded once at startup.</summary>
public sealed class SettingsService(ConfigStore store)
{
    public AppSettings Current { get; private set; } = new();

    public async Task LoadAsync(CancellationToken ct = default) =>
        Current = await store.LoadSettingsAsync(ct).ConfigureAwait(false);

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await store.SaveSettingsAsync(settings, ct).ConfigureAwait(false);
        Current = settings;
    }
}
