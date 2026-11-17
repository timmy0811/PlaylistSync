using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlaylistSync.App.Services;
using PlaylistSync.App.ViewModels;
using PlaylistSync.Core;
using PlaylistSync.Core.Covers;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Locations;
using PlaylistSync.Core.Logging;
using PlaylistSync.Core.Playback;
using PlaylistSync.Core.Playlists;
using PlaylistSync.Core.Sources;
using PlaylistSync.Core.Storage;
using PlaylistSync.Core.Sync;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.ForCurrentPlatform();
        paths.EnsureCreated();

        using var services = ConfigureServices(paths);
        App.Services = services;

        var log = services.GetRequiredService<ILogger<App>>();
        log.LogInformation("Starting Playlist Sync {Version}", typeof(Program).Assembly.GetName().Version);

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Unhandled exception, shutting down");
            throw;
        }
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

    private static ServiceProvider ConfigureServices(AppPaths paths)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(paths);
        // Information until settings.json is read; then the level from the settings (changeable at runtime).
        var logLevel = new LogLevelSwitch();
        sc.AddSingleton(logLevel);
        sc.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddFilter((_, _, level) => logLevel.IsEnabled(level));
            b.AddProvider(new FileLoggerProvider(paths.LogDir));
#if DEBUG
            b.AddConsole();
#endif
        });
        sc.AddSingleton<ConfigStore>();
        sc.AddSingleton<ManifestStore>();
        sc.AddSingleton<IProcessRunner, ProcessRunner>();
        sc.AddSingleton<ToolLocator>();
        sc.AddSingleton<ToolChecker>();
        sc.AddSingleton<IPlaylistSource, YtDlpPlaylistSource>();
        sc.AddSingleton(TimeProvider.System);
        sc.AddSingleton<ITrackDownloader, YtDlpTrackDownloader>();
        sc.AddSingleton<IReplacementFinder, YtDlpReplacementFinder>();
        sc.AddSingleton(sp => new FolderResolver(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<ILogger<FolderResolver>>(),
            Path.Combine(paths.DataDir, "downloads")));
        sc.AddSingleton(sp => new BrowserCookieCache(
            sp.GetRequiredService<IProcessRunner>(),
            BrowserCookieCache.DefaultDirectory(paths, Environment.GetEnvironmentVariable),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<BrowserCookieCache>>()));
        sc.AddSingleton<SyncService>();
        sc.AddSingleton<SingleDownloader>();
        sc.AddSingleton(sp => new CoverCache(
            paths,
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            sp.GetRequiredService<ILogger<CoverCache>>()));
        sc.AddSingleton<ICoverStore>(sp => sp.GetRequiredService<CoverCache>());
        sc.AddSingleton<CoverLoader>();
        sc.AddSingleton(sp => new TrackThumbnails(
            paths,
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<ILogger<TrackThumbnails>>(),
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
        sc.AddSingleton<ITrackThumbnails>(sp => sp.GetRequiredService<TrackThumbnails>());
        sc.AddSingleton<ThumbnailLoader>();
        sc.AddSingleton<PlayerLauncher>();
        sc.AddSingleton<PlaylistLibrary>();
        sc.AddSingleton<SettingsService>();
        sc.AddSingleton<IDialogService, DialogService>();
        sc.AddSingleton<ToolboxViewModel>();
        sc.AddSingleton<MainWindowViewModel>();
        return sc.BuildServiceProvider();
    }
}
