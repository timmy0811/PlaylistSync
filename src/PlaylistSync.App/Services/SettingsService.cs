using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using PlaylistSync.Core.Logging;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Storage;

namespace PlaylistSync.App.Services;

/// <summary>
/// Holds the current <see cref="AppSettings"/> for the UI. Loaded once at startup. Loading and saving apply the
/// settings that take effect immediately (theme, log level); everything else is read when a sync starts.
/// </summary>
public sealed class SettingsService(ConfigStore store, LogLevelSwitch logLevel)
{
    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised on the UI thread after the settings were saved.</summary>
    public event Action<AppSettings>? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Current = await store.LoadSettingsAsync(ct).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => Apply(Current));
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await store.SaveSettingsAsync(settings, ct).ConfigureAwait(false);
        Current = settings;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Apply(settings);
            Changed?.Invoke(settings);
        });
    }

    private void Apply(AppSettings settings)
    {
        logLevel.MinimumLevel = settings.Ui.LogLevel;
        ApplyTheme(settings.Ui.Theme);
    }

    public static void ApplyTheme(ThemeMode mode)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
