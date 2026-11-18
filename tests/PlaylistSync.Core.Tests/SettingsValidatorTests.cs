using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Settings;

namespace PlaylistSync.Core.Tests;

public class SettingsValidatorTests
{
    private static SettingsValidation V(DownloadOptions o, bool playlistScope = false, params string[] existingFiles) =>
        SettingsValidator.ValidateDownload(o, existingFiles.Contains, playlistScope);

    [Fact]
    public void Defaults_are_valid()
    {
        var v = V(new DownloadOptions());
        Assert.True(v.IsValid);
        Assert.Empty(v.Warnings);
        Assert.True(SettingsValidator.ValidateApp(new AppSettings()).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%(title)s.%(ext)s")]
    [InlineData("%(artist,uploader)s/%(album|Singles)s/%(title)s.%(ext)s")]
    [InlineData("100%% %(title)s.%(ext)s")]
    public void Valid_templates(string? template) => Assert.Null(SettingsValidator.TemplateError(template));

    [Theory]
    [InlineData("/music/%(title)s.%(ext)s", "full path")]
    [InlineData("~/%(title)s.%(ext)s", "full path")]
    [InlineData("../%(title)s.%(ext)s", "outside")]
    [InlineData("a\\..\\%(title)s.%(ext)s", "outside")]
    [InlineData("%(title)s.mp3", ".%(ext)s")]
    [InlineData("%(title)s", ".%(ext)s")]
    [InlineData("%(title.%(ext)s", "not closed")]
    public void Invalid_templates(string template, string expected) =>
        Assert.Contains(expected, SettingsValidator.TemplateError(template));

    [Fact]
    public void Windows_drive_paths_are_rejected_on_windows() =>
        Assert.Equal(OperatingSystem.IsWindows(), SettingsValidator.TemplateError(@"C:\m\%(title)s.%(ext)s") is not null);

    [Fact]
    public void Template_error_is_reported_under_its_field() =>
        Assert.NotNull(V(new DownloadOptions { FilenameTemplate = "x.mp3" }).Error(nameof(DownloadOptions.FilenameTemplate)));

    [Theory]
    [InlineData(-1, 320, nameof(DownloadOptions.VbrLevel))]
    [InlineData(11, 320, nameof(DownloadOptions.VbrLevel))]
    [InlineData(0, 16, nameof(DownloadOptions.CbrKbps))]
    [InlineData(0, 512, nameof(DownloadOptions.CbrKbps))]
    public void Quality_ranges(int vbr, int cbr, string field) =>
        Assert.NotNull(V(new DownloadOptions { VbrLevel = vbr, CbrKbps = cbr }).Error(field));

    [Fact]
    public void Cookie_file_must_be_set_and_exist()
    {
        const string field = nameof(DownloadOptions.CookiesFile);
        Assert.Contains("Choose", V(new DownloadOptions { CookieMode = CookieMode.File }).Error(field));
        Assert.Contains("doesn't exist", V(new DownloadOptions { CookieMode = CookieMode.File, CookiesFile = "/x/c.txt" }).Error(field));
        Assert.True(V(new DownloadOptions { CookieMode = CookieMode.File, CookiesFile = " /x/c.txt " }, false, "/x/c.txt").IsValid);
        // Only checked when that mode is active.
        Assert.True(V(new DownloadOptions { CookieMode = CookieMode.None, CookiesFile = "/nope" }).IsValid);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("netscape", false)]
    [InlineData("firefox", true)]
    [InlineData("Chrome", true)]
    public void Cookie_browser(string? browser, bool valid) =>
        Assert.Equal(valid, V(new DownloadOptions { CookieMode = CookieMode.Browser, CookiesBrowser = browser }).IsValid);

    [Theory]
    [InlineData("socks5://127.0.0.1:1080", true)]
    [InlineData("http://user:pw@host:3128", true)]
    [InlineData("HTTPS://host", true)]
    [InlineData("127.0.0.1:1080", false)]
    [InlineData("ftp://host", false)]
    public void Proxy(string proxy, bool valid) =>
        Assert.Equal(valid, V(new DownloadOptions { Proxy = proxy }).IsValid);

    [Theory]
    [InlineData("500K", true)]
    [InlineData("2M", true)]
    [InlineData("1.5m", true)]
    [InlineData("4.2MiB", true)]
    [InlineData("100000", true)]
    [InlineData("fast", false)]
    [InlineData("2 MB/s", false)]
    public void Rate_limit(string rate, bool valid) =>
        Assert.Equal(valid, V(new DownloadOptions { RateLimit = rate }).IsValid);

    [Fact]
    public void Network_ranges()
    {
        Assert.False(V(new DownloadOptions { Retries = -1 }).IsValid);
        Assert.False(V(new DownloadOptions { Retries = SettingsValidator.MaxRetries + 1 }).IsValid);
        Assert.False(V(new DownloadOptions { SleepIntervalSeconds = -5 }).IsValid);
        Assert.True(V(new DownloadOptions { SleepIntervalSeconds = 5, Retries = 0 }).IsValid);
    }

    [Fact]
    public void Network_group_is_not_checked_for_a_playlist()
    {
        var o = new DownloadOptions { Proxy = "nonsense", CookieMode = CookieMode.File, Retries = -1 };
        Assert.False(V(o).IsValid);
        Assert.True(V(o, playlistScope: true).IsValid);
    }

    [Fact]
    public void Extra_args_with_an_open_quote_are_an_error()
    {
        var v = V(new DownloadOptions { ExtraArgs = "--postprocessor-args \"ffmpeg:-ar 44100" });
        Assert.Equal("A quote is not closed.", v.Error(nameof(DownloadOptions.ExtraArgs)));
    }

    [Theory]
    [InlineData("-o x.mp3")]
    [InlineData("--output=%(id)s.%(ext)s")]
    [InlineData("--no-progress")]
    [InlineData("--embed-subs -q")]
    public void Extra_args_that_interfere_are_a_warning(string extra)
    {
        var v = V(new DownloadOptions { ExtraArgs = extra });
        Assert.True(v.IsValid);
        Assert.NotNull(v.Warning(nameof(DownloadOptions.ExtraArgs)));
    }

    [Fact]
    public void Harmless_extra_args_pass()
    {
        var v = V(new DownloadOptions { ExtraArgs = "--embed-subs --audio-multistreams" });
        Assert.True(v.IsValid);
        Assert.Empty(v.Warnings);
    }

    [Fact]
    public void Cover_in_wav_is_a_warning()
    {
        var v = V(new DownloadOptions { Format = AudioFormat.Wav, EmbedThumbnail = true });
        Assert.True(v.IsValid);
        Assert.NotNull(v.Warning(nameof(DownloadOptions.EmbedThumbnail)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(SettingsValidator.MaxConcurrency, true)]
    [InlineData(SettingsValidator.MaxConcurrency + 1, false)]
    public void Concurrency(int value, bool valid)
    {
        Assert.Equal(valid, SettingsValidator.ValidateApp(new AppSettings { Concurrency = value }).IsValid);
        Assert.Equal(valid, SettingsValidator.ValidateApp(new AppSettings { VideoConcurrency = value }).IsValid);
    }

    [Fact]
    public void Tool_paths_must_be_full_paths_or_empty()
    {
        var full = OperatingSystem.IsWindows() ? @"C:\tools\yt-dlp.exe" : "/usr/bin/yt-dlp";
        Assert.True(SettingsValidator.ValidateApp(new AppSettings { Tools = new ToolSettings { YtDlpPath = full, FfmpegPath = " " } }).IsValid);
        var v = SettingsValidator.ValidateApp(new AppSettings { Tools = new ToolSettings { FfmpegPath = "ffmpeg" } });
        Assert.NotNull(v.Error(nameof(ToolSettings.FfmpegPath)));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("~", true)]
    [InlineData("~/Music", true)]
    [InlineData("Music", false)]
    [InlineData("./music", false)]
    public void Default_directory(string? value, bool valid)
    {
        var v = SettingsValidator.ValidateApp(new AppSettings { DefaultDirectory = value }, home: Path.GetTempPath());
        Assert.Equal(valid, v.Error(nameof(AppSettings.DefaultDirectory)) is null);
    }

    [Fact]
    public void Absolute_default_directory_is_valid() =>
        Assert.True(SettingsValidator.ValidateApp(new AppSettings { DefaultDirectory = Path.GetTempPath() }).IsValid);

    [Fact]
    public void Splitter_reports_an_unclosed_quote()
    {
        Assert.Equal(["a", "b c"], CommandLineSplitter.Split("a 'b c", out var open));
        Assert.True(open);
        CommandLineSplitter.Split("a \"b c\"", out open);
        Assert.False(open);
    }
}

public class EffectiveOptionsTests
{
    [Fact]
    public void Without_override_the_global_options_are_used()
    {
        var global = new DownloadOptions { Format = AudioFormat.Flac };
        Assert.Same(global, DownloadOptions.Effective(global, null));
    }

    [Fact]
    public void Override_keeps_its_own_options_but_takes_the_network_group_from_global()
    {
        var global = new DownloadOptions
        {
            CookieMode = CookieMode.Browser, CookiesBrowser = "firefox", CookiesBrowserProfile = "work",
            Proxy = "socks5://p:1", RateLimit = "1M", SleepIntervalSeconds = 3, Retries = 4,
            Format = AudioFormat.Mp3, ExtraArgs = "--global",
        };
        var own = new DownloadOptions { Format = AudioFormat.Opus, TrackNumberPrefix = true, ExtraArgs = "--own", Proxy = "http://old:1" };

        var e = DownloadOptions.Effective(global, own);

        Assert.Equal((AudioFormat.Opus, true, "--own"), (e.Format, e.TrackNumberPrefix, e.ExtraArgs));
        Assert.Equal((CookieMode.Browser, "firefox", "work"), (e.CookieMode, e.CookiesBrowser, e.CookiesBrowserProfile));
        Assert.Equal(("socks5://p:1", "1M", 3, 4), (e.Proxy, e.RateLimit, e.SleepIntervalSeconds, e.Retries));
    }

    [Fact]
    public void Entry_helper_uses_the_settings()
    {
        var settings = new AppSettings { Download = new DownloadOptions { Format = AudioFormat.M4a } };
        var entry = new PlaylistEntry { Name = "n", Url = "u", Directory = "d" };
        Assert.Equal(AudioFormat.M4a, entry.EffectiveOptions(settings).Format);
        Assert.Equal(AudioFormat.Ogg, (entry with { Overrides = new DownloadOptions { Format = AudioFormat.Ogg } }).EffectiveOptions(settings).Format);
    }

}
