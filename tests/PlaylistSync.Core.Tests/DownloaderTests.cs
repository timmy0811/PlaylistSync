using Microsoft.Extensions.Logging.Abstractions;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

public class DownloadOutputParserTests
{
    // Real output of yt-dlp 2026.10.26 downloading jNQXAC9IVRw with the TrackDownload args (paths shortened).
    private static string[] Fixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Parses_recorded_download_progress()
    {
        var lines = Fixture("download-stdout.txt");
        var progress = lines.Select(DownloadOutputParser.ParseDownload).ToList();

        Assert.All(progress.Take(lines.Length - 1), p => Assert.NotNull(p));
        Assert.All(progress.OfType<DownloadProgress>(), p => Assert.Equal(DownloadPhase.Downloading, p.Phase));

        var first = progress[0]!;
        Assert.Equal(1024d / 252182 * 100, first.Percent!.Value, 6);
        Assert.Equal(447952.3671255736, first.BytesPerSecond);
        Assert.Equal(0, first.EtaSeconds);

        var percents = progress.OfType<DownloadProgress>().Select(p => p.Percent!.Value).ToList();
        Assert.Equal(percents.Order(), percents);
        Assert.Equal(100, percents[^1]);
    }

    [Fact]
    public void Parses_recorded_final_path()
    {
        var lines = Fixture("download-stdout.txt");
        Assert.Equal("/music/.playlist-sync-tmp/jNQXAC9IVRw/jawed - Me at the zoo.mp3", DownloadOutputParser.ParseFile(lines[^1]));
        Assert.Null(DownloadOutputParser.ParseDownload(lines[^1]));
        Assert.Null(DownloadOutputParser.ParseFile(lines[0]));
    }

    [Fact]
    public void Parses_recorded_postprocessing_steps()
    {
        var steps = Fixture("download-stderr.txt").Select(DownloadOutputParser.ParsePostProcess).OfType<DownloadProgress>().ToList();

        Assert.All(steps, s => Assert.Equal(DownloadPhase.Processing, s.Phase));
        Assert.Equal(["Preparing cover", "Converting", "Writing tags", "Embedding cover"], steps.Select(s => s.Step));
    }

    [Fact]
    public void Unknown_size_and_estimate()
    {
        Assert.Null(DownloadOutputParser.ParseDownload("""PSYNC-DL {"status": "downloading", "downloaded_bytes": 10}""")!.Percent);
        Assert.Equal(50, DownloadOutputParser.ParseDownload(
            """PSYNC-DL {"status": "downloading", "downloaded_bytes": 10, "total_bytes": null, "total_bytes_estimate": 20}""")!.Percent);
    }

    [Theory]
    [InlineData("PSYNC-DL not json")]
    [InlineData("PSYNC-DL []")]
    [InlineData("[download] 50% of 3MiB")]
    [InlineData("")]
    public void Ignores_garbage(string line)
    {
        Assert.Null(DownloadOutputParser.ParseDownload(line));
        Assert.Null(DownloadOutputParser.ParsePostProcess(line));
        Assert.Null(DownloadOutputParser.ParseFile(line));
    }

    [Fact]
    public void Unknown_postprocessor_is_shown_by_name()
    {
        Assert.Equal("FixupM4a", DownloadOutputParser.ParsePostProcess("PSYNC-PP started FixupM4a")!.Step);
        Assert.Null(DownloadOutputParser.ParsePostProcess("PSYNC-PP finished FixupM4a"));
    }
}

public class FilePlacerTests
{
    [Fact]
    public void Moves_file_and_returns_relative_path()
    {
        using var tmp = new TempDir();
        var src = Directory.CreateDirectory(Path.Combine(tmp.Path, "tmp", "id1")).FullName;
        var file = Path.Combine(src, "A - B.mp3");
        File.WriteAllText(file, "x");

        var rel = FilePlacer.MoveIntoPlace(file, src, tmp.Path, "id1", restrictFilenames: false);

        Assert.Equal("A - B.mp3", rel);
        Assert.True(File.Exists(tmp.File("A - B.mp3")));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Keeps_subfolders_from_the_template()
    {
        using var tmp = new TempDir();
        var src = Directory.CreateDirectory(Path.Combine(tmp.Path, "tmp", "id1", "Artist")).Parent!.FullName;
        var file = Path.Combine(src, "Artist", "Song.mp3");
        File.WriteAllText(file, "x");

        Assert.Equal("Artist/Song.mp3", FilePlacer.MoveIntoPlace(file, src, tmp.Path, "id1", false));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "Artist", "Song.mp3")));
    }

    [Theory]
    [InlineData(false, "Song [id2].mp3", "Song [id2] (2).mp3")]
    [InlineData(true, "Song_id2.mp3", "Song_id2_2.mp3")]
    public void Never_overwrites_on_collision(bool restrict, string second, string third)
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("Song.mp3"), "user file");
        var src = Directory.CreateDirectory(Path.Combine(tmp.Path, "tmp")).FullName;

        File.WriteAllText(Path.Combine(src, "Song.mp3"), "new 1");
        Assert.Equal(second, FilePlacer.MoveIntoPlace(Path.Combine(src, "Song.mp3"), src, tmp.Path, "id2", restrict));

        File.WriteAllText(Path.Combine(src, "Song.mp3"), "new 2");
        Assert.Equal(third, FilePlacer.MoveIntoPlace(Path.Combine(src, "Song.mp3"), src, tmp.Path, "id2", restrict));

        Assert.Equal("user file", File.ReadAllText(tmp.File("Song.mp3")));
        Assert.Equal("new 1", File.ReadAllText(tmp.File(second)));
        Assert.Equal("new 2", File.ReadAllText(tmp.File(third)));
    }

    [Fact]
    public void Rejects_file_outside_source_root()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("x.mp3"), "x");
        var src = Directory.CreateDirectory(Path.Combine(tmp.Path, "tmp")).FullName;
        Assert.Throws<IOException>(() => FilePlacer.MoveIntoPlace(tmp.File("x.mp3"), src, tmp.Path, "id", false));
    }
}

public class TrackDownloaderTests
{
    private static readonly DownloadTools Tools = new(
        new ToolLocation(ToolKind.YtDlp, "/usr/bin/yt-dlp", "yt-dlp", ToolSource.SystemPath),
        new ToolLocation(ToolKind.Ffmpeg, "/usr/bin/ffmpeg", "ffmpeg", ToolSource.SystemPath),
        null);

    private static DownloadRequest Request(TempDir dir, string id = "vid1") =>
        new(id, dir.Path, Path.Combine(dir.Path, ".playlist-sync-tmp"), new DownloadOptions(), Tools);

    private static YtDlpTrackDownloader Downloader(FakeProcessRunner runner) => new(runner, NullLogger<YtDlpTrackDownloader>.Instance);

    /// <summary>Simulates yt-dlp: writes the file into the -P dir and prints progress + the final path.</summary>
    private static FakeProcessRunner FakeYtDlp(string fileName = "Artist - Song.mp3") => new(req =>
    {
        var outDir = req.Arguments[req.Arguments.ToList().IndexOf("-P") + 1];
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "Artist - Song.webm"), "source");
        var path = Path.Combine(outDir, fileName);
        File.WriteAllText(path, "audio");

        req.OnStdoutLine?.Invoke("""PSYNC-DL {"status": "downloading", "downloaded_bytes": 50, "total_bytes": 100}""");
        req.OnStdoutLine?.Invoke("""PSYNC-DL {"status": "finished", "downloaded_bytes": 100, "total_bytes": 100}""");
        req.OnStderrLine?.Invoke("PSYNC-PP started ExtractAudio");
        req.OnStderrLine?.Invoke("PSYNC-PP finished ExtractAudio");
        req.OnStdoutLine?.Invoke("PSYNC-FILE " + path);
        return FakeProcessRunner.Ok("");
    });

    [Fact]
    public async Task Downloads_into_target_and_cleans_temp()
    {
        using var dir = new TempDir();
        var runner = FakeYtDlp();
        var reports = new List<DownloadProgress>();

        var result = await Downloader(runner).DownloadAsync(Request(dir), new SyncProgress(reports), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("Artist - Song.mp3", result.File);
        Assert.Equal("audio", File.ReadAllText(dir.File("Artist - Song.mp3")));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, ".playlist-sync-tmp", "vid1")));

        var req = runner.Requests.Single();
        Assert.Equal("/usr/bin/yt-dlp", req.FileName);
        Assert.False(req.CaptureStdout);
        Assert.Equal(YtDlpArgsBuilder.Environment, req.Environment);
        Assert.Equal(Path.Combine(dir.Path, ".playlist-sync-tmp", "vid1"), req.Arguments[req.Arguments.ToList().IndexOf("-P") + 1]);

        Assert.Equal(DownloadPhase.Starting, reports[0].Phase);
        Assert.Contains(reports, r => r.Phase == DownloadPhase.Downloading && r.Percent == 100);
        Assert.Contains(reports, r => r is { Phase: DownloadPhase.Processing, Step: "Converting" });
    }

    [Fact]
    public async Task Failure_keeps_temp_dir_for_resume()
    {
        using var dir = new TempDir();
        var runner = new FakeProcessRunner(req =>
        {
            var outDir = req.Arguments[req.Arguments.ToList().IndexOf("-P") + 1];
            File.WriteAllText(Path.Combine(outDir, "x.webm.part"), "partial");
            return FakeProcessRunner.Fail("ERROR: unable to download video data: HTTP Error 403: Forbidden");
        });

        var result = await Downloader(runner).DownloadAsync(Request(dir), null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadErrorKind.Other, result.Error!.Kind);
        Assert.Equal("unable to download video data: HTTP Error 403: Forbidden", result.Error.Message);
        Assert.True(File.Exists(Path.Combine(dir.Path, ".playlist-sync-tmp", "vid1", "x.webm.part")));
    }

    [Fact]
    public async Task Success_without_reported_file_is_a_failure()
    {
        using var dir = new TempDir();
        var result = await Downloader(new FakeProcessRunner(_ => FakeProcessRunner.Ok("")))
            .DownloadAsync(Request(dir), null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("did not produce", result.Error!.Message);
    }

    [Fact]
    public async Task Missing_executable_is_tool_missing()
    {
        using var dir = new TempDir();
        var runner = new FakeProcessRunner(_ => throw new ProcessStartException("Could not start 'yt-dlp'", new Exception()));
        var result = await Downloader(runner).DownloadAsync(Request(dir), null, TestContext.Current.CancellationToken);
        Assert.Equal(DownloadErrorKind.ToolMissing, result.Error!.Kind);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Downloader(FakeYtDlp()).DownloadAsync(Request(dir), null, cts.Token));
    }

    [Fact]
    public async Task Collision_appends_video_id()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("Artist - Song.mp3"), "other");

        var result = await Downloader(FakeYtDlp()).DownloadAsync(Request(dir), null, TestContext.Current.CancellationToken);

        Assert.Equal("Artist - Song [vid1].mp3", result.File);
        Assert.Equal("other", File.ReadAllText(dir.File("Artist - Song.mp3")));
    }

    [Theory]
    [InlineData("ERROR: [youtube] AAAAAAAAAA1: This video is unavailable", DownloadErrorKind.Unavailable)]
    [InlineData("ERROR: [youtube] abc: Private video. Sign in if you've been granted access to this video", DownloadErrorKind.Unavailable)]
    [InlineData("ERROR: [youtube] abc: Video unavailable. This video has been removed by the uploader", DownloadErrorKind.Unavailable)]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm your age. This video may be inappropriate for some users.", DownloadErrorKind.NeedsCookies)]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you're not a bot.", DownloadErrorKind.NeedsCookies)]
    [InlineData("ERROR: [youtube] abc: Join this channel to get access to members-only content like this video", DownloadErrorKind.NeedsCookies)]
    [InlineData("ERROR: [youtube] abc: Video unavailable. This content isn't available, try again later.", DownloadErrorKind.Other)]
    [InlineData("ERROR: Postprocessing: ffprobe and ffmpeg not found. Please install or provide the path using --ffmpeg-location", DownloadErrorKind.ToolMissing)]
    [InlineData("ERROR: [youtube] abc: The uploader has not made this video available in your country", DownloadErrorKind.Other)]
    public void Classifies_errors(string stderr, DownloadErrorKind expected)
    {
        Assert.Equal(expected, YtDlpTrackDownloader.DescribeError("WARNING: something\n" + stderr + "\n").Kind);
    }

    [Fact]
    public void Error_hints()
    {
        Assert.Contains("rate-limiting", YtDlpTrackDownloader.DescribeError("ERROR: HTTP Error 429: Too Many Requests").Message);
        Assert.Contains("python-mutagen", YtDlpTrackDownloader.DescribeError(
            "ERROR: Postprocessing: module mutagen was not found. Please install using `python3 -m pip install mutagen`").Message);
        Assert.Contains("Settings → Network", YtDlpTrackDownloader.DescribeError("ERROR: [youtube] x: Sign in to confirm your age").Message);
    }

    private sealed class SyncProgress(List<DownloadProgress> sink) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            lock (sink) sink.Add(value);
        }
    }
}
