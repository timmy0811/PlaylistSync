using Microsoft.Extensions.Logging.Abstractions;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Sources;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

public class FlatPlaylistParserTests
{
    // Trimmed real output of `yt-dlp --flat-playlist -J` (2026.10.26), plus a private, a deleted and a duplicate entry.
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "flat-playlist.json"));

    [Fact]
    public void Parses_real_output()
    {
        var p = FlatPlaylistParser.Parse(Fixture());

        Assert.Equal("PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI", p.Id);
        Assert.Equal("Popular Music Videos", p.Title);
        Assert.Equal("Music", p.Uploader);
        Assert.Equal("https://www.youtube.com/playlist?list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI", p.WebpageUrl);
        Assert.Equal(6, p.ReportedCount);

        Assert.Equal(["fOT0BUpITw8", "AAAAAAAAAA1", "NFvDHYMzj9U", "8c7KOGxeY3w", "AAAAAAAAAA2"], p.Tracks.Select(t => t.Id));
        Assert.Equal([0, 1, 2, 3, 4], p.Tracks.Select(t => t.Index));

        var first = p.Tracks[0];
        Assert.Equal("BELLAKEO (Video Oficial) - Peso Pluma, Anitta", first.Title);
        Assert.Equal("Peso Pluma", first.Uploader);
        Assert.Equal(235, first.DurationSec);
        Assert.True(first.IsAvailable);

        Assert.Equal("Arcángel - FN8 ( Video Lyric )", p.Tracks[2].Title);
    }

    [Fact]
    public void Private_and_deleted_entries_are_unavailable_not_skipped()
    {
        var p = FlatPlaylistParser.Parse(Fixture());

        Assert.Equal("private video", p.Tracks[1].UnavailableReason);
        Assert.Equal("deleted video", p.Tracks[4].UnavailableReason);
        Assert.Equal(3, p.Tracks.Count(t => t.IsAvailable));
    }

    [Theory]
    [InlineData("""{"availability":"private"}""", "private video")]
    [InlineData("""{"live_status":"is_upcoming"}""", "not released yet")]
    [InlineData("""{"title":"[Unavailable video]"}""", "unavailable video")]
    [InlineData("""{"availability":"unlisted"}""", null)]
    [InlineData("""{"availability":"needs_auth"}""", null)]
    public void Unavailability_rules(string extra, string? expected)
    {
        var entry = MergeEntry("""{"_type":"url","ie_key":"Youtube","id":"abc","title":"T"}""", extra);
        var p = FlatPlaylistParser.Parse($$"""{"_type":"playlist","id":"PL1","entries":[{{entry}}]}""");
        Assert.Equal(expected, p.Tracks.Single().UnavailableReason);
    }

    [Fact]
    public void Missing_fields_are_tolerated_and_uploader_falls_back()
    {
        var p = FlatPlaylistParser.Parse("""
            {"_type":"playlist","entries":[
              {"_type":"url","id":"a","uploader":"Up","duration":12.5},
              {"_type":"url","id":"b","title":null,"duration":null},
              null, 42, {"_type":"url","title":"no id"}
            ]}
            """);

        Assert.Null(p.Id);
        Assert.Null(p.ReportedCount);
        Assert.Equal(["a", "b"], p.Tracks.Select(t => t.Id));
        Assert.Equal("Up", p.Tracks[0].Uploader);
        Assert.Equal(12.5, p.Tracks[0].DurationSec);
        Assert.Null(p.Tracks[1].Title);
    }

    [Fact]
    public void Non_video_entries_are_skipped()
    {
        var p = FlatPlaylistParser.Parse("""
            {"_type":"playlist","entries":[
              {"_type":"url","ie_key":"YoutubeTab","id":"UCxyz"},
              {"_type":"url","ie_key":"Youtube","id":"v1"}
            ]}
            """);
        Assert.Equal(["v1"], p.Tracks.Select(t => t.Id));
    }

    [Fact]
    public void Empty_playlist_is_valid()
    {
        var p = FlatPlaylistParser.Parse("""{"_type":"playlist","id":"PL1","title":"Empty","entries":[]}""");
        Assert.Empty(p.Tracks);
    }

    [Fact]
    public void Channel_url_is_rejected()
    {
        var ex = Assert.Throws<PlaylistFetchException>(() => FlatPlaylistParser.Parse("""
            {"_type":"playlist","id":"@YouTube","entries":[
              {"_type":"playlist","id":"UC1","title":"YouTube - Videos"},
              {"_type":"playlist","id":"UC1","title":"YouTube - Shorts"}
            ]}
            """));
        Assert.Equal(FetchErrorKind.NotAPlaylist, ex.Kind);
        Assert.Contains("channel", ex.Message);
    }

    [Fact]
    public void Single_video_is_rejected()
    {
        var ex = Assert.Throws<PlaylistFetchException>(() =>
            FlatPlaylistParser.Parse("""{"id":"dQw4w9WgXcQ","title":"Rick","formats":[]}"""));
        Assert.Equal(FetchErrorKind.NotAPlaylist, ex.Kind);
        Assert.Contains("single video", ex.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    public void Garbage_is_bad_output(string json)
    {
        var ex = Assert.Throws<PlaylistFetchException>(() => FlatPlaylistParser.Parse(json));
        Assert.Equal(FetchErrorKind.BadOutput, ex.Kind);
    }

    private static string MergeEntry(string a, string b) => a.TrimEnd('}') + "," + b.TrimStart('{');
}

public class YtDlpPlaylistSourceTests
{
    private const string Url = "https://www.youtube.com/playlist?list=PL1";
    private const string Json = """{"_type":"playlist","id":"PL1","title":"Mix","entries":[{"_type":"url","ie_key":"Youtube","id":"v1","title":"Song"}]}""";

    [Fact]
    public async Task Fetches_with_builder_args_and_utf8_environment()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok(Json));
        var p = await Source(runner, "/usr/bin/yt-dlp", "/usr/bin/deno").FetchAsync(Url, Context(),
            TestContext.Current.CancellationToken);

        Assert.Equal("Mix", p.Title);
        var request = runner.Requests.Single();
        Assert.Equal("/usr/bin/yt-dlp", request.FileName);
        Assert.Equal(YtDlpArgsBuilder.PlaylistFetch(Url, new DownloadOptions(),
            new ToolLocation(ToolKind.JsRuntime, "/usr/bin/deno", "deno", ToolSource.SystemPath)), request.Arguments);
        Assert.Equal("utf-8", request.Environment!["PYTHONIOENCODING"]);
    }

    [Fact]
    public async Task Missing_yt_dlp_fails_without_running()
    {
        var runner = new FakeProcessRunner(_ => throw new InvalidOperationException("must not run"));
        var ex = await Assert.ThrowsAsync<PlaylistFetchException>(() =>
            Source(runner).FetchAsync(Url, Context(), TestContext.Current.CancellationToken));
        Assert.Equal(FetchErrorKind.ToolMissing, ex.Kind);
    }

    [Fact]
    public async Task Non_youtube_url_fails_without_running()
    {
        var runner = new FakeProcessRunner(_ => throw new InvalidOperationException("must not run"));
        var ex = await Assert.ThrowsAsync<PlaylistFetchException>(() =>
            Source(runner, "/usr/bin/yt-dlp").FetchAsync("https://open.spotify.com/playlist/x", Context(),
                TestContext.Current.CancellationToken));
        Assert.Equal(FetchErrorKind.NotAPlaylist, ex.Kind);
    }

    [Fact]
    public async Task Start_failure_is_tool_missing()
    {
        var runner = new FakeProcessRunner(r => throw new ProcessStartException($"Could not start '{r.FileName}'", new Exception()));
        var ex = await Assert.ThrowsAsync<PlaylistFetchException>(() =>
            Source(runner, "/usr/bin/yt-dlp").FetchAsync(Url, Context(), TestContext.Current.CancellationToken));
        Assert.Equal(FetchErrorKind.ToolMissing, ex.Kind);
    }

    [Fact]
    public async Task Yt_dlp_error_becomes_clean_message()
    {
        // Real stderr for a nonexistent playlist id.
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Fail("""
            WARNING: [youtube:tab] YouTube said: ERROR - Request contains an invalid argument.
            ERROR: [youtube:tab] PLdoesnotexist123: Unable to download API page: HTTP Error 400: Bad Request (caused by <HTTPError 400: Bad Request>)
            """));
        var ex = await Assert.ThrowsAsync<PlaylistFetchException>(() =>
            Source(runner, "/usr/bin/yt-dlp").FetchAsync(Url, Context(), TestContext.Current.CancellationToken));

        Assert.Equal(FetchErrorKind.Other, ex.Kind);
        Assert.StartsWith("Unable to download API page: HTTP Error 400", ex.Message);
    }

    [Theory]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.")]
    [InlineData("ERROR: [youtube:tab] LL: The playlist does not exist or is private. Use --cookies for the authentication.")]
    public void Auth_errors_point_to_cookie_settings(string stderr)
    {
        var ex = YtDlpPlaylistSource.DescribeError(stderr);
        Assert.Equal(FetchErrorKind.NeedsCookies, ex.Kind);
        Assert.Contains("Settings", ex.Message);
        Assert.DoesNotContain("ERROR:", ex.Message);
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=WL")]
    [InlineData("https://www.youtube.com/playlist?list=LL")]
    [InlineData("https://music.youtube.com/playlist?list=LM")]
    public void Personal_playlists_point_to_cookie_settings(string url)
    {
        // Real stderr for Watch Later without cookies.
        var ex = YtDlpPlaylistSource.DescribeError("ERROR: [youtube:tab] WL: YouTube said: The playlist does not exist.", url);
        Assert.Equal(FetchErrorKind.NeedsCookies, ex.Kind);
    }

    [Fact]
    public void Error_without_error_line_uses_last_line()
    {
        Assert.Equal("Traceback boom", YtDlpPlaylistSource.DescribeError("line one\nTraceback boom\n").Message);
        Assert.Contains("without an error message", YtDlpPlaylistSource.DescribeError("").Message);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok(Json));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Source(runner, "/usr/bin/yt-dlp").FetchAsync(Url, Context(), cts.Token));
    }

    private static SourceContext Context() => new(new ToolSettings(), new DownloadOptions());

    private static YtDlpPlaylistSource Source(IProcessRunner runner, params string[] executables)
    {
        var set = new HashSet<string>(executables);
        var locator = new ToolLocator("/data/bin", isWindows: false, k => k == "PATH" ? "/usr/bin" : null, set.Contains);
        return new YtDlpPlaylistSource(locator, runner, NullLogger<YtDlpPlaylistSource>.Instance);
    }
}

public class YouTubeUrlTests
{
    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PL1", true)]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_x", true)]
    [InlineData("http://youtube.com/watch?v=a&list=PL1", true)]
    [InlineData("https://m.youtube.com/playlist?list=PL1", true)]
    [InlineData("https://youtu.be/abc?list=PL1", true)]
    [InlineData("  https://www.youtube.com/playlist?list=PL1  ", true)]
    [InlineData("https://open.spotify.com/playlist/37i9", false)]
    [InlineData("https://youtube.com.evil.example/playlist?list=PL1", false)]
    [InlineData("ftp://youtube.com/x", false)]
    [InlineData("youtube.com/playlist?list=PL1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Recognises_youtube(string? url, bool expected) => Assert.Equal(expected, YouTubeUrl.IsYouTube(url));

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PL1", "PL1")]
    [InlineData("https://www.youtube.com/watch?v=abc&list=PL2&index=3", "PL2")]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_x%2Dy", "OLAK5uy_x-y")]
    [InlineData("https://www.youtube.com/watch?v=abc", null)]
    [InlineData("https://www.youtube.com/playlist?list=", null)]
    [InlineData("https://www.youtube.com/playlist?playlist=PL1", null)]
    [InlineData("https://example.com/?list=PL1", null)]
    public void Extracts_playlist_id(string url, string? expected) => Assert.Equal(expected, YouTubeUrl.PlaylistId(url));
}

public class YtDlpArgsBuilderFetchTests
{
    private const string Url = "https://www.youtube.com/playlist?list=PL1";

    [Fact]
    public void Minimal_fetch_args()
    {
        Assert.Equal(
            ["--ignore-config", "--flat-playlist", "--dump-single-json", "--yes-playlist", "--", Url],
            YtDlpArgsBuilder.PlaylistFetch(Url, new DownloadOptions(), null));
    }

    [Fact]
    public void Url_is_trimmed_and_always_last_after_double_dash()
    {
        var args = YtDlpArgsBuilder.PlaylistFetch("  " + Url + " ", new DownloadOptions { Proxy = "socks5://h:1" }, null);
        Assert.Equal(["--", Url], args.TakeLast(2));
    }

    [Theory]
    [InlineData("deno", "/usr/bin/deno")]
    [InlineData("node", "/opt/node/bin/node")]
    public void Js_runtime_is_passed_by_name_and_path(string name, string path)
    {
        var js = new ToolLocation(ToolKind.JsRuntime, path, name, ToolSource.SystemPath);
        var args = YtDlpArgsBuilder.PlaylistFetch(Url, new DownloadOptions(), js);
        AssertPair(args, "--js-runtimes", $"{name}:{path}");
    }

    [Fact]
    public void Missing_configured_js_runtime_is_not_passed()
    {
        var js = new ToolLocation(ToolKind.JsRuntime, "/nope/deno", "deno", ToolSource.Configured, Exists: false);
        Assert.DoesNotContain("--js-runtimes", YtDlpArgsBuilder.PlaylistFetch(Url, new DownloadOptions(), js));
    }

    [Fact]
    public void Cookies_file()
    {
        var o = new DownloadOptions { CookieMode = CookieMode.File, CookiesFile = "/home/u/cookies.txt" };
        AssertPair(YtDlpArgsBuilder.PlaylistFetch(Url, o, null), "--cookies", "/home/u/cookies.txt");
    }

    [Theory]
    [InlineData("firefox", null, "firefox")]
    [InlineData("firefox", "  ", "firefox")]
    [InlineData("chromium", "Profile 1", "chromium:Profile 1")]
    public void Cookies_from_browser(string browser, string? profile, string expected)
    {
        var o = new DownloadOptions { CookieMode = CookieMode.Browser, CookiesBrowser = browser, CookiesBrowserProfile = profile };
        AssertPair(YtDlpArgsBuilder.PlaylistFetch(Url, o, null), "--cookies-from-browser", expected);
    }

    [Fact]
    public void Cookie_mode_none_ignores_leftover_paths()
    {
        var o = new DownloadOptions { CookieMode = CookieMode.None, CookiesFile = "/x", CookiesBrowser = "firefox" };
        var args = YtDlpArgsBuilder.PlaylistFetch(Url, o, null);
        Assert.DoesNotContain("--cookies", args);
        Assert.DoesNotContain("--cookies-from-browser", args);
    }

    [Fact]
    public void Cookie_mode_without_value_adds_nothing()
    {
        var o = new DownloadOptions { CookieMode = CookieMode.File, CookiesFile = " " };
        Assert.DoesNotContain("--cookies", YtDlpArgsBuilder.PlaylistFetch(Url, o, null));
    }

    [Fact]
    public void Proxy()
    {
        var o = new DownloadOptions { Proxy = "socks5://127.0.0.1:1080" };
        AssertPair(YtDlpArgsBuilder.PlaylistFetch(Url, o, null), "--proxy", "socks5://127.0.0.1:1080");
    }

    [Fact]
    public void Blank_url_throws() =>
        Assert.Throws<ArgumentException>(() => YtDlpArgsBuilder.PlaylistFetch(" ", new DownloadOptions(), null));

    private static void AssertPair(IReadOnlyList<string> args, string option, string value)
    {
        var i = args.ToList().IndexOf(option);
        Assert.True(i >= 0, $"{option} missing");
        Assert.Equal(value, args[i + 1]);
        Assert.True(i + 1 < args.ToList().IndexOf("--"), "options must come before --");
    }
}
