using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlaylistSync.App.ViewModels;
using PlaylistSync.Core;
using PlaylistSync.Core.Logging;
using PlaylistSync.Core.Storage;
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
        sc.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(new FileLoggerProvider(paths.LogDir));
#if DEBUG
            b.AddConsole();
            b.SetMinimumLevel(LogLevel.Debug);
#endif
        });
        sc.AddSingleton<ConfigStore>();
        sc.AddSingleton<ManifestStore>();
        sc.AddSingleton<IProcessRunner, ProcessRunner>();
        sc.AddSingleton<ToolLocator>();
        sc.AddSingleton<ToolChecker>();
        sc.AddSingleton<MainWindowViewModel>();
        return sc.BuildServiceProvider();
    }
}
