using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PlaylistSync.Core.Covers;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

public class TrackThumbnailsTests : IDisposable
{
    private static readonly ToolLocation Ffmpeg = new(ToolKind.Ffmpeg, "/usr/bin/ffmpeg", "ffmpeg", ToolSource.SystemPath);
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private TrackThumbnails Create(FakeProcessRunner runner, FakeHandler? http = null) =>
        new(new AppPaths(_dir.File("cfg"), _dir.File("data")), runner, NullLogger<TrackThumbnails>.Instance,
            http is null ? null : new HttpClient(http));

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public HttpStatusCode Status = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            var content = new ByteArrayContent([0xFF, 0xD8, 0xFF]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(Status) { Content = content });
        }
    }

    /// <summary>ffmpeg that finds no cover in audio files (as for a file downloaded without "embed cover").</summary>
    private static FakeProcessRunner FfmpegWithoutEmbeddedCover() => new(r =>
    {
        if (r.Arguments.Contains("-map")) return FakeProcessRunner.Fail("Stream map '0:v:0' matches no streams.");
        File.WriteAllText(r.Arguments[^1], "jpeg");
        return FakeProcessRunner.Ok("");
    });

    private string AudioFile()
    {
        var path = _dir.File("Artist - Song.mp3");
        File.WriteAllText(path, "audio");
        return path;
    }

    private string TrackTemp(params string[] files)
    {
        var dir = _dir.File("tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), "x");
        return dir;
    }

    /// <summary>Pretends to be ffmpeg: writes the output file (the last argument).</summary>
    private static FakeProcessRunner WritingFfmpeg() => new(r =>
    {
        File.WriteAllText(r.Arguments[^1], "jpeg");
        return FakeProcessRunner.Ok("");
    });

    [Fact]
    public void Finds_the_thumbnail_yt_dlp_wrote_and_ignores_other_files()
    {
        var dir = TrackTemp("Artist - Song.mp3", "thumbnail.webp", "thumbnail.jpg", "thumbnail.part");
        Assert.Equal(Path.Combine(dir, "thumbnail.jpg"), TrackThumbnails.FindSource(dir));
        Assert.Null(TrackThumbnails.FindSource(TrackTemp("Artist - Song.mp3", "thumbnail.part")));
        Assert.Null(TrackThumbnails.FindSource(_dir.File("does-not-exist")));
    }

    [Fact]
    public void Ffmpeg_crops_square_and_scales_to_one_small_jpeg()
    {
        var args = TrackThumbnails.FfmpegArgs("/t/thumbnail.webp", "/d/id.jpg.tmp");
        Assert.Equal("/t/thumbnail.webp", args[args.ToList().IndexOf("-i") + 1]);
        Assert.Contains($"scale={TrackThumbnails.Size}:{TrackThumbnails.Size}", args[args.ToList().IndexOf("-vf") + 1]);
        Assert.StartsWith("crop='min(iw,ih)':'min(iw,ih)'", args[args.ToList().IndexOf("-vf") + 1]);
        Assert.Equal("1", args[args.ToList().IndexOf("-frames:v") + 1]);
        Assert.Equal("/d/id.jpg.tmp", args[^1]);
    }

    [Fact]
    public async Task Creates_the_thumbnail_under_the_track_id()
    {
        var runner = WritingFfmpeg();
        var store = Create(runner);

        Assert.True(await store.CreateFromTempDirAsync("abc_-123XYZ", TrackTemp("thumbnail.jpg"), Ffmpeg, CancellationToken.None));

        Assert.Equal(store.PathFor("abc_-123XYZ"), store.Find("abc_-123XYZ"));
        Assert.Equal("/usr/bin/ffmpeg", Assert.Single(runner.Requests).FileName);
        Assert.Empty(Directory.EnumerateFiles(store.Dir, "*.tmp"));
    }

    [Fact]
    public async Task Without_a_thumbnail_ffmpeg_is_not_run()
    {
        var runner = WritingFfmpeg();
        var store = Create(runner);

        Assert.False(await store.CreateFromTempDirAsync("abc", TrackTemp("song.mp3"), Ffmpeg, CancellationToken.None));
        Assert.Empty(runner.Requests);
        Assert.Null(store.Find("abc"));
    }

    [Fact]
    public async Task A_failing_ffmpeg_leaves_nothing_behind()
    {
        var runner = new FakeProcessRunner(r =>
        {
            File.WriteAllText(r.Arguments[^1], "half");
            return FakeProcessRunner.Fail("Invalid data found when processing input");
        });
        var store = Create(runner);

        Assert.False(await store.CreateFromTempDirAsync("abc", TrackTemp("thumbnail.webp"), Ffmpeg, CancellationToken.None));
        Assert.Null(store.Find("abc"));
        Assert.Empty(Directory.EnumerateFiles(store.Dir));
    }

    [Fact]
    public void Download_keeps_the_thumbnail_in_the_temp_dir_only_when_asked()
    {
        var o = new DownloadOptions();
        var plain = YtDlpArgsBuilder.TrackDownload("vid", "/tmp/vid", o, Ffmpeg, null, null, 2, isWindows: false);
        Assert.DoesNotContain("--write-thumbnail", plain);
        Assert.DoesNotContain(plain, a => a.StartsWith("thumbnail:", StringComparison.Ordinal));

        var keep = YtDlpArgsBuilder.TrackDownload("vid", "/tmp/vid", o, Ffmpeg, null, null, 2, isWindows: false, keepThumbnail: true);
        Assert.Contains("--write-thumbnail", keep);
        Assert.Contains("thumbnail:thumbnail.%(ext)s", keep);
        Assert.Contains("--embed-thumbnail", keep); // embedding is unaffected
        Assert.Equal("--", keep[^2]);
    }

    [Fact]
    public async Task Backfill_takes_the_cover_embedded_in_the_audio_file_first()
    {
        var runner = WritingFfmpeg();
        var http = new FakeHandler();
        var store = Create(runner, http);
        var audio = AudioFile();

        Assert.Equal(ThumbnailSource.AudioFile, await store.CreateMissingAsync("abc", audio, null, Ffmpeg, CancellationToken.None));

        var args = Assert.Single(runner.Requests).Arguments;
        Assert.Equal(audio, args[args.ToList().IndexOf("-i") + 1]);
        Assert.Equal("0:v:0", args[args.ToList().IndexOf("-map") + 1]);
        Assert.Empty(http.Requests);
        Assert.NotNull(store.Find("abc"));
    }

    [Fact]
    public async Task Backfill_falls_back_to_the_youtube_image_of_the_file_source()
    {
        var runner = FfmpegWithoutEmbeddedCover();
        var http = new FakeHandler();
        var store = Create(runner, http);

        // The file came from a replacement upload: its image, stored under the playlist's id.
        Assert.Equal(ThumbnailSource.YouTube, await store.CreateMissingAsync("abc", AudioFile(), "replacement1", Ffmpeg, CancellationToken.None));

        Assert.Equal("https://i.ytimg.com/vi/replacement1/mqdefault.jpg", Assert.Single(http.Requests).ToString());
        Assert.Equal(2, runner.Requests.Count);
        Assert.NotNull(store.Find("abc"));
        Assert.Equal(["abc.jpg"], Directory.EnumerateFiles(store.Dir).Select(Path.GetFileName)); // temp files gone
    }

    [Fact]
    public async Task Backfill_without_a_reachable_file_uses_youtube()
    {
        var http = new FakeHandler();
        var store = Create(WritingFfmpeg(), http);

        Assert.Equal(ThumbnailSource.YouTube, await store.CreateMissingAsync("abc", null, null, Ffmpeg, CancellationToken.None));
        Assert.Equal(ThumbnailSource.YouTube, await Create(WritingFfmpeg(), http)
            .CreateMissingAsync("def", _dir.File("missing.mp3"), null, Ffmpeg, CancellationToken.None));
        Assert.Equal(["/vi/abc/mqdefault.jpg", "/vi/def/mqdefault.jpg"], http.Requests.Select(u => u.AbsolutePath));
    }

    [Fact]
    public async Task Backfill_gives_up_on_a_track_for_the_session()
    {
        var runner = FfmpegWithoutEmbeddedCover();
        var http = new FakeHandler { Status = HttpStatusCode.NotFound };
        var store = Create(runner, http);

        Assert.Equal(ThumbnailSource.None, await store.CreateMissingAsync("gone", AudioFile(), null, Ffmpeg, CancellationToken.None));
        Assert.Equal(ThumbnailSource.None, await store.CreateMissingAsync("gone", AudioFile(), null, Ffmpeg, CancellationToken.None));

        Assert.Single(runner.Requests);
        Assert.Single(http.Requests);
        Assert.Null(store.Find("gone"));
    }

    [Fact]
    public async Task Backfill_leaves_existing_thumbnails_alone()
    {
        var store = Create(WritingFfmpeg());
        Assert.True(await store.CreateFromTempDirAsync("abc", TrackTemp("thumbnail.jpg"), Ffmpeg, CancellationToken.None));
        var runner = WritingFfmpeg();
        var again = Create(runner);

        Assert.Equal(ThumbnailSource.Existing, await again.CreateMissingAsync("abc", AudioFile(), null, Ffmpeg, CancellationToken.None));
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task Ids_that_are_not_video_ids_never_become_paths_or_urls(string id)
    {
        var runner = WritingFfmpeg();
        var http = new FakeHandler();
        var store = Create(runner, http);

        Assert.Equal(ThumbnailSource.None, await store.CreateMissingAsync(id, AudioFile(), null, Ffmpeg, CancellationToken.None));
        Assert.False(await store.CreateFromTempDirAsync(id, TrackTemp("thumbnail.jpg"), Ffmpeg, CancellationToken.None));
        Assert.Null(store.Find(id));
        Assert.Empty(runner.Requests);
        Assert.Empty(http.Requests);
    }
}
