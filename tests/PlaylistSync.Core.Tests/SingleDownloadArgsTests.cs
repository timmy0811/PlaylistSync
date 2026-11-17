using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

public class SingleDownloadArgsTests
{
    private const string Url = "https://www.youtube.com/watch?v=abc123";
    private static readonly ToolLocation Ffmpeg = new(ToolKind.Ffmpeg, "/usr/bin/ffmpeg", "ffmpeg", ToolSource.SystemPath);

    private static IReadOnlyList<string> Build(SingleDownloadKind kind, AudioFormat format = AudioFormat.Mp3, int? maxHeight = null,
        DownloadOptions? o = null, bool windows = false, string url = Url) =>
        YtDlpArgsBuilder.SingleDownload(
            new SingleDownloadSpec(url, "/home/u/Downloads", "/data/toolbox-tmp", kind, format, maxHeight, o ?? new DownloadOptions()),
            Ffmpeg, null, windows);

    private static string? ValueAfter(IReadOnlyList<string> args, string option)
    {
        var i = args.ToList().IndexOf(option);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    private static IEnumerable<string> ValuesAfter(IReadOnlyList<string> args, string option) =>
        args.Select((a, i) => (a, i)).Where(x => x.a == option && x.i + 1 < args.Count).Select(x => args[x.i + 1]);

    [Fact]
    public void Audio_uses_chosen_format_and_global_quality_and_template()
    {
        var o = new DownloadOptions { QualityMode = QualityMode.Cbr, CbrKbps = 192, FilenameTemplate = "%(title)s.%(ext)s" };
        var args = Build(SingleDownloadKind.Audio, AudioFormat.Opus, o: o);

        Assert.Equal("--ignore-config", args[0]);
        Assert.Contains("--no-playlist", args);
        Assert.Contains("-x", args);
        Assert.Equal("bestaudio/best", ValueAfter(args, "-f"));
        Assert.Equal("opus", ValueAfter(args, "--audio-format"));
        Assert.Equal("192K", ValueAfter(args, "--audio-quality"));
        Assert.Equal("%(title)s.%(ext)s", ValueAfter(args, "-o"));
        Assert.Equal(["/home/u/Downloads", "temp:/data/toolbox-tmp"], ValuesAfter(args, "-P"));
        Assert.Equal("/usr/bin/ffmpeg", ValueAfter(args, "--ffmpeg-location"));
    }

    [Fact]
    public void Url_comes_last_after_double_dash()
    {
        var args = Build(SingleDownloadKind.Audio, url: "  -https://evil  ");
        Assert.Equal("--", args[^2]);
        Assert.Equal("-https://evil", args[^1]);
    }

    [Fact]
    public void Wav_audio_skips_quality_and_cover()
    {
        var args = Build(SingleDownloadKind.Audio, AudioFormat.Wav);
        Assert.Equal("wav", ValueAfter(args, "--audio-format"));
        Assert.DoesNotContain("--audio-quality", args);
        Assert.DoesNotContain("--embed-thumbnail", args);
    }

    [Fact]
    public void Video_prefers_resolution_limit_and_mp4_without_extracting_audio()
    {
        var args = Build(SingleDownloadKind.Video, maxHeight: 720);

        Assert.DoesNotContain("-x", args);
        Assert.DoesNotContain("--audio-format", args);
        Assert.DoesNotContain("--parse-metadata", args);
        Assert.Equal("bv*+ba/b", ValueAfter(args, "-f"));
        Assert.Equal("res:720,ext:mp4:m4a", ValueAfter(args, "-S"));
        Assert.Equal("mp4/mkv", ValueAfter(args, "--merge-output-format"));
        Assert.Equal(YtDlpArgsBuilder.SingleVideoFilenameTemplate, ValueAfter(args, "-o"));
        Assert.Contains("--embed-thumbnail", args);
        Assert.DoesNotContain("--ppa", args); // no square crop for video thumbnails
        Assert.Contains("--embed-chapters", args);
    }

    [Fact]
    public void Video_best_has_no_resolution_limit()
    {
        Assert.Equal("ext:mp4:m4a", ValueAfter(Build(SingleDownloadKind.Video), "-S"));
    }

    [Fact]
    public void Network_options_and_windows_filenames_are_applied()
    {
        var o = new DownloadOptions { CookieMode = CookieMode.Browser, CookiesBrowser = "firefox", Proxy = "socks5://h:1", RateLimit = "2M" };
        var args = Build(SingleDownloadKind.Video, o: o, windows: true);
        Assert.Equal("firefox", ValueAfter(args, "--cookies-from-browser"));
        Assert.Equal("socks5://h:1", ValueAfter(args, "--proxy"));
        Assert.Equal("2M", ValueAfter(args, "--limit-rate"));
        Assert.Contains("--windows-filenames", args);
    }
}
