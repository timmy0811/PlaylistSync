using Microsoft.Extensions.Logging.Abstractions;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Storage;

namespace PlaylistSync.Core.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ConfigStore _store;
    private readonly AppPaths _paths;

    public ConfigStoreTests()
    {
        _paths = new AppPaths(_dir.File("cfg"), _dir.File("data"));
        _store = new ConfigStore(_paths, NullLogger<ConfigStore>.Instance);
    }

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_settings_yield_defaults_with_mp3()
    {
        var s = await _store.LoadSettingsAsync(Ct);
        Assert.Equal(AudioFormat.Mp3, s.Download.Format);
        Assert.False(s.Download.WriteM3u);
        Assert.False(s.Download.TrackNumberPrefix);
        Assert.Equal(2, s.Concurrency);
        Assert.Equal(1, s.VideoConcurrency);
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        var s = new AppSettings
        {
            Concurrency = 4,
            Download = new DownloadOptions { Format = AudioFormat.Opus, CookieMode = CookieMode.Browser, CookiesBrowser = "firefox", WriteM3u = true },
            Tools = new ToolSettings { YtDlpPath = "/opt/yt-dlp" },
            Ui = new UiSettings { Theme = ThemeMode.Dark },
        };
        await _store.SaveSettingsAsync(s, Ct);
        var loaded = await _store.LoadSettingsAsync(Ct);
        Assert.Equal(s, loaded);
        Assert.Contains("\"opus\"", await File.ReadAllTextAsync(_paths.SettingsFile, Ct));
    }

    [Fact]
    public async Task Corrupt_settings_are_moved_aside_and_defaults_returned()
    {
        Directory.CreateDirectory(_paths.ConfigDir);
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ not json", Ct);

        var s = await _store.LoadSettingsAsync(Ct);

        Assert.Equal(new AppSettings(), s);
        Assert.False(File.Exists(_paths.SettingsFile));
        Assert.Single(Directory.GetFiles(_paths.ConfigDir, "settings.json.corrupt-*"));
    }

    [Fact]
    public async Task Library_round_trip()
    {
        var entry = new PlaylistEntry
        {
            Name = "Chill", Url = "https://www.youtube.com/playlist?list=PL123", Directory = "/music/chill",
            Overrides = new DownloadOptions { Format = AudioFormat.M4a },
        };
        await _store.SaveLibraryAsync(new Library { Playlists = [entry] }, Ct);

        var lib = await _store.LoadLibraryAsync(Ct);

        var loaded = Assert.Single(lib.Playlists);
        Assert.Equal(entry, loaded);
        Assert.False(File.Exists(_paths.LibraryFile + ".tmp"));
    }
}
