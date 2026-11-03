using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

public class DownloadArgsTests
{
    private const string Temp = "/music/.playlist-sync-tmp/vid123";
    private static readonly ToolLocation Ffmpeg = new(ToolKind.Ffmpeg, "/usr/bin/ffmpeg", "ffmpeg", ToolSource.SystemPath);

    private static IReadOnlyList<string> Build(DownloadOptions? o = null, int? trackNumber = null, int width = 2, bool windows = false,
        ToolLocation? ffmpeg = null, ToolLocation? js = null) =>
        YtDlpArgsBuilder.TrackDownload("vid123", Temp, o ?? new DownloadOptions(), ffmpeg ?? Ffmpeg, js, trackNumber, width, windows);

    private static string? ValueAfter(IReadOnlyList<string> args, string option)
    {
        var i = args.ToList().IndexOf(option);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    private static IEnumerable<string> ValuesAfter(IReadOnlyList<string> args, string option) =>
        args.Select((a, i) => (a, i)).Where(x => x.a == option && x.i + 1 < args.Count).Select(x => args[x.i + 1]);

    [Fact]
    public void Defaults_mp3_vbr0_with_metadata_and_square_cover()
    {
        var args = Build();

        Assert.Equal("--ignore-config", args[0]);
        Assert.Contains("--no-playlist", args);
        Assert.Contains("-x", args);
        Assert.Equal("bestaudio/best", ValueAfter(args, "-f"));
        Assert.Equal("mp3", ValueAfter(args, "--audio-format"));
        Assert.Equal("0", ValueAfter(args, "--audio-quality"));
        Assert.Contains("--embed-metadata", args);
        Assert.Contains("--embed-thumbnail", args);
        Assert.Equal("jpg", ValueAfter(args, "--convert-thumbnails"));
        Assert.Equal(YtDlpArgsBuilder.CropThumbnailArgs, ValueAfter(args, "--ppa"));
        Assert.Equal(YtDlpArgsBuilder.ParseArtistTitleArgs, ValueAfter(args, "--parse-metadata"));
        Assert.Contains("--no-embed-chapters", args);
        Assert.DoesNotContain("--sponsorblock-remove", args);
        Assert.DoesNotContain("--restrict-filenames", args);
        Assert.DoesNotContain("--windows-filenames", args);
        Assert.Contains("--no-mtime", args);
    }

    [Fact]
    public void Url_is_last_after_double_dash()
    {
        var args = Build(new DownloadOptions { ExtraArgs = "--foo" });
        Assert.Equal("--", args[^2]);
        Assert.Equal("https://www.youtube.com/watch?v=vid123", args[^1]);
    }

    [Fact]
    public void Output_goes_to_temp_dir_with_template()
    {
        var args = Build();
        Assert.Equal(Temp, ValueAfter(args, "-P"));
        Assert.Equal(DownloadOptions.DefaultFilenameTemplate, ValueAfter(args, "-o"));
    }

    [Fact]
    public void Machine_readable_progress_and_final_path()
    {
        var args = Build();
        Assert.Contains("--newline", args);
        Assert.Contains("--progress", args);
        Assert.Equal(
            ["download:PSYNC-DL %(progress)j", "postprocess:PSYNC-PP %(progress.status)s %(progress.postprocessor)s"],
            ValuesAfter(args, "--progress-template"));
        Assert.Equal("after_move:PSYNC-FILE %(filepath)s", ValueAfter(args, "--print"));
    }

    [Theory]
    [InlineData(AudioFormat.Mp3, "mp3")]
    [InlineData(AudioFormat.M4a, "m4a")]
    [InlineData(AudioFormat.Opus, "opus")]
    [InlineData(AudioFormat.Ogg, "vorbis")]
    [InlineData(AudioFormat.Flac, "flac")]
    [InlineData(AudioFormat.Wav, "wav")]
    public void Audio_format(AudioFormat format, string expected)
    {
        Assert.Equal(expected, ValueAfter(Build(new DownloadOptions { Format = format }), "--audio-format"));
    }

    [Fact]
    public void Best_keeps_the_original_codec()
    {
        var args = Build(new DownloadOptions { Format = AudioFormat.Best });
        Assert.Contains("-x", args);
        Assert.DoesNotContain("--audio-format", args);
        Assert.DoesNotContain("--audio-quality", args);
    }

    [Theory]
    [InlineData(AudioFormat.Flac)]
    [InlineData(AudioFormat.Wav)]
    public void Lossless_formats_have_no_quality(AudioFormat format)
    {
        Assert.DoesNotContain("--audio-quality", Build(new DownloadOptions { Format = format }));
    }

    [Theory]
    [InlineData(QualityMode.Vbr, 5, 320, "5")]
    [InlineData(QualityMode.Vbr, 42, 320, "10")]
    [InlineData(QualityMode.Vbr, -3, 320, "0")]
    [InlineData(QualityMode.Cbr, 0, 192, "192K")]
    [InlineData(QualityMode.Cbr, 0, 9999, "320K")]
    public void Quality(QualityMode mode, int vbr, int cbr, string expected)
    {
        var o = new DownloadOptions { QualityMode = mode, VbrLevel = vbr, CbrKbps = cbr };
        Assert.Equal(expected, ValueAfter(Build(o), "--audio-quality"));
    }

    [Fact]
    public void Metadata_switches_off()
    {
        var args = Build(new DownloadOptions { EmbedMetadata = false, EmbedThumbnail = false, ParseArtistTitle = false });
        Assert.Contains("--no-embed-metadata", args);
        Assert.DoesNotContain("--embed-metadata", args);
        Assert.DoesNotContain("--embed-thumbnail", args);
        Assert.DoesNotContain("--convert-thumbnails", args);
        Assert.DoesNotContain("--ppa", args);
        Assert.DoesNotContain("--parse-metadata", args);
    }

    [Fact]
    public void Cover_without_crop()
    {
        var args = Build(new DownloadOptions { CropThumbnailSquare = false });
        Assert.Contains("--embed-thumbnail", args);
        Assert.DoesNotContain("--convert-thumbnails", args);
        Assert.DoesNotContain("--ppa", args);
    }

    [Fact]
    public void Wav_never_embeds_a_cover()
    {
        Assert.DoesNotContain("--embed-thumbnail", Build(new DownloadOptions { Format = AudioFormat.Wav }));
    }

    [Fact]
    public void Chapters_and_sponsorblock()
    {
        var args = Build(new DownloadOptions { EmbedChapters = true, SponsorBlockRemoveOfftopic = true });
        Assert.Contains("--embed-chapters", args);
        Assert.Equal("music_offtopic", ValueAfter(args, "--sponsorblock-remove"));
    }

    [Fact]
    public void Filename_flags()
    {
        Assert.Contains("--restrict-filenames", Build(new DownloadOptions { RestrictFilenames = true }));
        Assert.Contains("--windows-filenames", Build(windows: true));
    }

    [Fact]
    public void Custom_template()
    {
        var args = Build(new DownloadOptions { FilenameTemplate = "%(title)s.%(ext)s" });
        Assert.Equal("%(title)s.%(ext)s", ValueAfter(args, "-o"));
        Assert.Equal(DownloadOptions.DefaultFilenameTemplate,
            ValueAfter(Build(new DownloadOptions { FilenameTemplate = "  " }), "-o"));
    }

    [Theory]
    [InlineData("%(title)s.%(ext)s", 7, 3, false, "007 - %(title)s.%(ext)s")]
    [InlineData("%(uploader)s/%(title)s.%(ext)s", 12, 2, false, "%(uploader)s/12 - %(title)s.%(ext)s")]
    [InlineData("%(title)s.%(ext)s", 3, 2, true, "03_-_%(title)s.%(ext)s")]
    public void Track_number_prefix(string template, int number, int width, bool restrict, string expected)
    {
        var o = new DownloadOptions { FilenameTemplate = template, TrackNumberPrefix = true, RestrictFilenames = restrict };
        Assert.Equal(expected, ValueAfter(Build(o, number, width), "-o"));
    }

    [Fact]
    public void Track_number_ignored_when_prefix_off()
    {
        Assert.Equal(DownloadOptions.DefaultFilenameTemplate, ValueAfter(Build(trackNumber: 5), "-o"));
    }

    [Fact]
    public void Network_settings()
    {
        var args = Build(new DownloadOptions { Retries = 3, RateLimit = " 2M ", SleepIntervalSeconds = 5, Proxy = "socks5://h:1" });
        Assert.Equal("3", ValueAfter(args, "--retries"));
        Assert.Equal("3", ValueAfter(args, "--fragment-retries"));
        Assert.Equal("2M", ValueAfter(args, "--limit-rate"));
        Assert.Equal("5", ValueAfter(args, "--sleep-interval"));
        Assert.Equal("socks5://h:1", ValueAfter(args, "--proxy"));
    }

    [Fact]
    public void No_rate_limit_or_sleep_by_default()
    {
        var args = Build();
        Assert.DoesNotContain("--limit-rate", args);
        Assert.DoesNotContain("--sleep-interval", args);
        Assert.Equal("10", ValueAfter(args, "--retries"));
    }

    [Fact]
    public void Cookies_from_file_and_browser()
    {
        Assert.Equal("/c.txt", ValueAfter(Build(new DownloadOptions { CookieMode = CookieMode.File, CookiesFile = "/c.txt" }), "--cookies"));
        Assert.Equal("firefox:work", ValueAfter(Build(new DownloadOptions
        {
            CookieMode = CookieMode.Browser, CookiesBrowser = "firefox", CookiesBrowserProfile = "work",
        }), "--cookies-from-browser"));
    }

    [Fact]
    public void Tool_locations()
    {
        var js = new ToolLocation(ToolKind.JsRuntime, "/opt/node", "node", ToolSource.Configured);
        var args = Build(js: js);
        Assert.Equal("/usr/bin/ffmpeg", ValueAfter(args, "--ffmpeg-location"));
        Assert.Equal("node:/opt/node", ValueAfter(args, "--js-runtimes"));

        var missing = Ffmpeg with { Exists = false };
        Assert.DoesNotContain("--ffmpeg-location", Build(ffmpeg: missing));
    }

    [Fact]
    public void Extra_args_come_last_and_can_override()
    {
        var args = Build(new DownloadOptions { ExtraArgs = "--audio-quality 5 --postprocessor-args \"ffmpeg:-ar 44100\"" });
        var tail = args.Take(args.Count - 2).TakeLast(4).ToList();
        Assert.Equal(["--audio-quality", "5", "--postprocessor-args", "ffmpeg:-ar 44100"], tail);
    }
}

public class CommandLineSplitterTests
{
    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("a b  c", new[] { "a", "b", "c" })]
    [InlineData("--x \"two words\" 'single quoted'", new[] { "--x", "two words", "single quoted" })]
    [InlineData("\"say \\\"hi\\\"\"", new[] { "say \"hi\"" })]
    [InlineData(@"C:\Users\me\cookies.txt", new[] { @"C:\Users\me\cookies.txt" })]
    [InlineData("\"\"", new[] { "" })]
    [InlineData("pre\"fix\"ed", new[] { "prefixed" })]
    [InlineData("\"unterminated rest", new[] { "unterminated rest" })]
    [InlineData("'it''s'", new[] { "its" })]
    public void Splits(string? input, string[] expected)
    {
        Assert.Equal(expected, CommandLineSplitter.Split(input));
    }
}
