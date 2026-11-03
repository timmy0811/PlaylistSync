using PlaylistSync.Core;

namespace PlaylistSync.Core.Tests;

public class AppPathsTests
{
    private static string Folder(Environment.SpecialFolder f) => f switch
    {
        Environment.SpecialFolder.UserProfile => "/home/u",
        Environment.SpecialFolder.ApplicationData => @"C:\Users\u\AppData\Roaming",
        Environment.SpecialFolder.LocalApplicationData => @"C:\Users\u\AppData\Local",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    [Fact]
    public void Linux_defaults_to_home_config_and_local_share()
    {
        var p = AppPaths.Resolve(false, _ => null, Folder);
        Assert.Equal(Path.Combine("/home/u", ".config", "playlist-sync"), p.ConfigDir);
        Assert.Equal(Path.Combine("/home/u", ".local", "share", "playlist-sync"), p.DataDir);
    }

    [Fact]
    public void Linux_respects_absolute_xdg_vars()
    {
        var env = new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = "/cfg", ["XDG_DATA_HOME"] = "/data" };
        var p = AppPaths.Resolve(false, k => env.GetValueOrDefault(k), Folder);
        Assert.Equal(Path.Combine("/cfg", "playlist-sync"), p.ConfigDir);
        Assert.Equal(Path.Combine("/data", "playlist-sync"), p.DataDir);
    }

    [Fact]
    public void Linux_ignores_relative_xdg_vars()
    {
        var p = AppPaths.Resolve(false, _ => "relative/dir", Folder);
        Assert.Equal(Path.Combine("/home/u", ".config", "playlist-sync"), p.ConfigDir);
    }

    [Fact]
    public void Windows_uses_appdata_folders()
    {
        var p = AppPaths.Resolve(true, _ => null, Folder);
        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Roaming", "PlaylistSync"), p.ConfigDir);
        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Local", "PlaylistSync"), p.DataDir);
    }

    [Fact]
    public void Derived_paths_live_under_base_dirs()
    {
        var p = new AppPaths("/c", "/d");
        Assert.Equal(Path.Combine("/c", "settings.json"), p.SettingsFile);
        Assert.Equal(Path.Combine("/c", "library.json"), p.LibraryFile);
        Assert.Equal(Path.Combine("/d", "bin"), p.BinDir);
        Assert.Equal(Path.Combine("/d", "logs"), p.LogDir);
    }
}
