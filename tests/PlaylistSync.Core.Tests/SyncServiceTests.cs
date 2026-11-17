using Microsoft.Extensions.Logging.Abstractions;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Sources;
using PlaylistSync.Core.Storage;
using PlaylistSync.Core.Sync;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

public class SyncServiceTests : IDisposable
{
    private const string Url = "https://www.youtube.com/playlist?list=PL1";
    private readonly TempDir _dir = new();
    private readonly ManifestStore _store = new();
    private readonly FakeSource _source = new();
    private readonly FakeDownloader _downloader = new();
    private readonly FakeFinder _finder = new();
    private bool _ffmpegInstalled = true;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private SyncService Service(PlaylistSync.Core.Covers.ICoverStore? covers = null, BrowserCookieCache? cookies = null)
    {
        var bin = Path.Combine(_dir.Path, "no-bin");
        var fake = Path.Combine(Path.GetTempPath(), "fake-path-dir");
        var executables = new HashSet<string> { Path.Combine(fake, "yt-dlp") };
        if (_ffmpegInstalled) executables.Add(Path.Combine(fake, "ffmpeg"));
        var locator = new ToolLocator(bin, OperatingSystem.IsWindows(), k => k == "PATH" ? fake : null, executables.Contains);
        return new SyncService([_source], _store, _downloader, locator, TimeProvider.System, NullLogger<SyncService>.Instance, covers,
            cookies: cookies, replacements: _finder);
    }

    private PlaylistEntry Entry(DownloadOptions? overrides = null) =>
        new() { Name = "My: Mix", Url = Url, Directory = _dir.Path, Overrides = overrides };

    private static RemotePlaylist Remote(params RemoteTrack[] tracks) => new("PL1", "Mix", "me", Url, tracks);

    private static RemoteTrack T(string id, int index, string? unavailable = null) =>
        new(id, "Title " + id, "Artist", 180, index, unavailable);

    private Task<Manifest?> SavedManifest() => _store.LoadAsync(_dir.Path, Ct);

    [Fact]
    public async Task First_sync_downloads_everything_available()
    {
        _source.Playlist = Remote(T("a", 0), T("b", 1, "private video"), T("c", 2));
        var events = new EventSink();

        var result = await Service().SyncAsync(Entry(), new AppSettings(), events, ct: Ct);

        Assert.Equal(2, result.Downloaded);
        Assert.Equal(0, result.Failed);
        Assert.Equal(["a", "c"], _downloader.Calls.Select(r => r.VideoId).Order());

        var saved = (await SavedManifest())!;
        Assert.Equal("PL1", saved.PlaylistId);
        Assert.Equal(TrackStatus.Synced, saved.Tracks["a"].Status);
        Assert.Equal("a.mp3", saved.Tracks["a"].File);
        Assert.NotNull(saved.Tracks["a"].DownloadedAt);
        Assert.Equal(TrackStatus.Unavailable, saved.Tracks["b"].Status);
        Assert.True(File.Exists(_dir.File("c.mp3")));

        Assert.Equal([SyncStage.Fetching, SyncStage.Planning, SyncStage.Downloading, SyncStage.Finishing],
            events.Of<SyncStageChanged>().Select(e => e.Stage));
        Assert.Equal(["a", "c"], events.Of<SyncPlanned>().Single().ToDownload);
        var finished = events.Of<TrackUpdated>().Where(e => e.Record.Status == TrackStatus.Synced).ToList();
        Assert.Equal(2, finished.Count);
        Assert.Equal([1, 2], finished.Select(e => e.Completed).Order());
        Assert.All(finished, e => Assert.Equal(2, e.Total));
        Assert.Contains(events.Of<TrackUpdated>(), e => e.Record.Status == TrackStatus.Downloading);
        Assert.Contains(events.Of<TrackProgress>(), e => e.Progress.Percent == 100);

        // Nothing left behind in the temp area.
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, SyncService.TempDirName)));
    }

    [Fact]
    public async Task Second_sync_downloads_nothing_and_needs_no_ffmpeg()
    {
        _source.Playlist = Remote(T("a", 0));
        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);

        _ffmpegInstalled = false;
        var result = await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);

        Assert.Equal(0, result.Downloaded);
        Assert.Single(_downloader.Calls);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public async Task Failures_are_recorded_and_retried_next_time()
    {
        _source.Playlist = Remote(T("ok", 0), T("bad", 1), T("gone", 2));
        _downloader.Respond = r => r.VideoId switch
        {
            "bad" => DownloadResult.Failure(DownloadErrorKind.Other, "HTTP Error 403"),
            "gone" => DownloadResult.Failure(DownloadErrorKind.Unavailable, "This video is unavailable"),
            _ => null,
        };

        var result = await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);

        Assert.Equal((1, 1, 1), (result.Downloaded, result.Failed, result.BecameUnavailable));
        var saved = (await SavedManifest())!;
        Assert.Equal(TrackStatus.Failed, saved.Tracks["bad"].Status);
        Assert.Equal("HTTP Error 403", saved.Tracks["bad"].LastError);
        Assert.Null(saved.Tracks["bad"].File);
        Assert.Equal(TrackStatus.Unavailable, saved.Tracks["gone"].Status);

        _downloader.Respond = _ => null;
        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);
        saved = (await SavedManifest())!;
        Assert.Equal(TrackStatus.Synced, saved.Tracks["bad"].Status);
        Assert.Null(saved.Tracks["bad"].LastError);
    }

    [Fact]
    public async Task Removed_remote_tracks_keep_their_file()
    {
        _source.Playlist = Remote(T("a", 0), T("b", 1));
        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);

        _source.Playlist = Remote(T("a", 0));
        var result = await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);

        Assert.Equal(TrackStatus.RemovedRemote, result.Manifest.Tracks["b"].Status);
        Assert.True(File.Exists(_dir.File("b.mp3")));
    }

    [Fact]
    public async Task Cancel_keeps_finished_tracks_and_requeues_the_rest()
    {
        _source.Playlist = Remote(T("a", 0), T("b", 1), T("c", 2));
        using var cts = new CancellationTokenSource();
        _downloader.Before = async (r, ct) =>
        {
            if (r.VideoId == "b")
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var settings = new AppSettings { Concurrency = 1 };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().SyncAsync(Entry(), settings, ct: cts.Token));

        var saved = (await SavedManifest())!;
        Assert.Equal(TrackStatus.Synced, saved.Tracks["a"].Status);
        Assert.Equal(TrackStatus.Pending, saved.Tracks["b"].Status);
        Assert.Equal(TrackStatus.Pending, saved.Tracks["c"].Status);
        Assert.DoesNotContain(_downloader.Calls, r => r.VideoId == "c");
        Assert.False(Service().IsRunning(_dir.Path));
    }

    [Fact]
    public async Task Cancel_keeps_temp_dirs_of_unfinished_tracks()
    {
        _source.Playlist = Remote(T("a", 0));
        using var cts = new CancellationTokenSource();
        _downloader.Before = async (r, ct) =>
        {
            Directory.CreateDirectory(r.TrackTempDirectory);
            await File.WriteAllTextAsync(Path.Combine(r.TrackTempDirectory, "a.webm.part"), "partial", ct);
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().SyncAsync(Entry(), new AppSettings(), ct: cts.Token));

        Assert.True(File.Exists(Path.Combine(_dir.Path, SyncService.TempDirName, "a", "a.webm.part")));
    }

    [Fact]
    public async Task Stale_temp_dirs_of_finished_tracks_are_removed()
    {
        var stale = Directory.CreateDirectory(Path.Combine(_dir.Path, SyncService.TempDirName, "old"));
        File.WriteAllText(Path.Combine(stale.FullName, "x.part"), "x");
        _source.Playlist = Remote(T("a", 0));

        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);

        Assert.False(Directory.Exists(Path.Combine(_dir.Path, SyncService.TempDirName)));
    }

    [Fact]
    public async Task Folder_of_another_playlist_needs_confirmation()
    {
        await _store.SaveAsync(_dir.Path, new Manifest { PlaylistId = "PLother", PlaylistTitle = "Other" }, Ct);
        _source.Playlist = Remote(T("a", 0));

        var ex = await Assert.ThrowsAsync<SyncException>(() => Service().SyncAsync(Entry(), new AppSettings(), ct: Ct));
        Assert.Equal(SyncErrorKind.DifferentPlaylist, ex.Kind);
        Assert.Contains("Other", ex.Message);
        Assert.Equal("PLother", (await SavedManifest())!.PlaylistId);
        Assert.Empty(_downloader.Calls);

        await Service().SyncAsync(Entry(), new AppSettings(), allowDifferentPlaylist: true, ct: Ct);
        Assert.Equal("PL1", (await SavedManifest())!.PlaylistId);
    }

    [Fact]
    public async Task Only_one_sync_per_folder()
    {
        _source.Playlist = Remote(T("a", 0));
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _downloader.Before = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var service = Service();

        var first = service.SyncAsync(Entry(), new AppSettings(), ct: Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(service.IsRunning(_dir.Path));

        var other = Entry() with { Directory = _dir.Path + Path.DirectorySeparatorChar };
        var ex = await Assert.ThrowsAsync<SyncException>(() => service.SyncAsync(other, new AppSettings(), ct: Ct));
        Assert.Equal(SyncErrorKind.AlreadyRunning, ex.Kind);

        release.SetResult();
        await first;
        Assert.False(service.IsRunning(_dir.Path));
    }

    [Fact]
    public async Task Missing_ffmpeg_stops_before_anything_is_written()
    {
        _ffmpegInstalled = false;
        _source.Playlist = Remote(T("a", 0));

        var ex = await Assert.ThrowsAsync<SyncException>(() => Service().SyncAsync(Entry(), new AppSettings(), ct: Ct));

        Assert.Equal(SyncErrorKind.ToolMissing, ex.Kind);
        Assert.Contains("ffmpeg", ex.Message);
        Assert.Null(await SavedManifest());
    }

    [Fact]
    public async Task Fetch_errors_are_wrapped()
    {
        _source.Error = new PlaylistFetchException(FetchErrorKind.NeedsCookies, "Sign in");

        var ex = await Assert.ThrowsAsync<SyncException>(() => Service().SyncAsync(Entry(), new AppSettings(), ct: Ct));

        Assert.Equal(SyncErrorKind.Fetch, ex.Kind);
        Assert.Equal("Sign in", ex.Message);
        Assert.IsType<PlaylistFetchException>(ex.InnerException);
    }

    [Fact]
    public async Task Corrupt_manifest_is_reported_and_left_alone()
    {
        File.WriteAllText(ManifestStore.PathFor(_dir.Path), "{ nope");
        _source.Playlist = Remote(T("a", 0));

        var ex = await Assert.ThrowsAsync<SyncException>(() => Service().SyncAsync(Entry(), new AppSettings(), ct: Ct));

        Assert.Equal(SyncErrorKind.Manifest, ex.Kind);
        Assert.Equal("{ nope", File.ReadAllText(ManifestStore.PathFor(_dir.Path)));
    }

    [Fact]
    public async Task Creates_missing_folder()
    {
        _source.Playlist = Remote(T("a", 0));
        var entry = Entry() with { Directory = Path.Combine(_dir.Path, "new", "folder") };

        await Service().SyncAsync(entry, new AppSettings(), ct: Ct);

        Assert.True(File.Exists(Path.Combine(entry.Directory, "a.mp3")));
    }

    [Fact]
    public async Task Uses_playlist_overrides_and_track_numbers()
    {
        _source.Playlist = Remote(Enumerable.Range(0, 120).Select(i => T("v" + i, i)).ToArray());
        var overrides = new DownloadOptions { Format = AudioFormat.Opus, Proxy = "http://ignored:1", Retries = 1 };
        var settings = new AppSettings { Download = new DownloadOptions { Proxy = "socks5://global:1080", Retries = 7 } };

        await Service().SyncAsync(Entry(overrides), settings, ct: Ct);

        // The playlist's own options, but the network settings always come from the global ones.
        Assert.All(_downloader.Calls, r => Assert.Equal(overrides with { Proxy = "socks5://global:1080", Retries = 7 }, r.Options));
        var v41 = _downloader.Calls.Single(r => r.VideoId == "v41");
        Assert.Equal(42, v41.TrackNumber);
        Assert.Equal(3, v41.TrackNumberWidth);
        Assert.Equal(Path.Combine(_dir.Path, SyncService.TempDirName), v41.TempRoot);
        Assert.EndsWith("ffmpeg", v41.Tools.Ffmpeg!.Path);
    }

    [Fact]
    public async Task Respects_concurrency()
    {
        _source.Playlist = Remote(Enumerable.Range(0, 8).Select(i => T("v" + i, i)).ToArray());
        int running = 0, max = 0;
        _downloader.Before = async (_, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref max, now);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref running);
        };

        await Service().SyncAsync(Entry(), new AppSettings { Concurrency = 3 }, ct: Ct);

        Assert.InRange(max, 2, 3);
        Assert.Equal(8, (await SavedManifest())!.Tracks.Values.Count(t => t.Status == TrackStatus.Synced));
    }

    [Fact]
    public async Task Writes_m3u_when_enabled()
    {
        _source.Playlist = Remote(T("a", 0), T("b", 1), T("c", 2));
        _downloader.Respond = r => r.VideoId == "b" ? DownloadResult.Failure(DownloadErrorKind.Other, "x") : null;

        await Service().SyncAsync(Entry(new DownloadOptions { WriteM3u = true }), new AppSettings(), ct: Ct);

        var m3u = File.ReadAllText(_dir.File("My_ Mix.m3u8"));
        Assert.Equal("#EXTM3U\n#EXTINF:180,Title a\na.mp3\n#EXTINF:180,Title c\nc.mp3\n", m3u);
    }

    [Fact]
    public async Task No_m3u_by_default()
    {
        _source.Playlist = Remote(T("a", 0));
        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.m3u8"));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    [Fact]
    public async Task Delete_removes_files_of_removed_tracks_and_forgets_them()
    {
        _source.Playlist = Remote(T("a", 0), T("b", 1));
        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);
        _source.Playlist = Remote(T("a", 0));
        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);

        var result = await Service().DeleteFilesAsync(Entry(), new AppSettings(), ["b"], Ct);

        Assert.Equal(["b"], result.Deleted);
        Assert.Empty(result.Failed);
        Assert.False(File.Exists(_dir.File("b.mp3")));
        Assert.True(File.Exists(_dir.File("a.mp3")));
        var saved = (await SavedManifest())!;
        Assert.False(saved.Tracks.ContainsKey("b"));
        Assert.Equal(TrackStatus.Synced, saved.Tracks["a"].Status);
    }

    [Fact]
    public async Task Delete_keeps_the_record_of_unavailable_tracks()
    {
        var manifest = new Manifest { PlaylistId = "PL1" };
        manifest.Tracks["u"] = new TrackRecord { Title = "Gone", File = "u.mp3", Status = TrackStatus.Unavailable, RemoteIndex = 0 };
        await _store.SaveAsync(_dir.Path, manifest, Ct);
        await File.WriteAllTextAsync(_dir.File("u.mp3"), "audio", Ct);

        var result = await Service().DeleteFilesAsync(Entry(), new AppSettings(), ["u"], Ct);

        Assert.Equal(["u"], result.Deleted);
        Assert.False(File.Exists(_dir.File("u.mp3")));
        var record = (await SavedManifest())!.Tracks["u"];
        Assert.Equal(TrackStatus.Unavailable, record.Status);
        Assert.Null(record.File);
    }

    [Fact]
    public async Task Delete_refuses_tracks_still_in_the_playlist_and_unknown_ids()
    {
        _source.Playlist = Remote(T("a", 0));
        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);

        var result = await Service().DeleteFilesAsync(Entry(), new AppSettings(), ["a", "nope"], Ct);

        Assert.Empty(result.Deleted);
        Assert.Equal(["a", "nope"], result.Failed.Select(f => f.Id));
        Assert.True(File.Exists(_dir.File("a.mp3")));
    }

    [Theory]
    [InlineData("../outside.mp3")]
    [InlineData(".playlist-sync.json")]
    public async Task Delete_refuses_paths_outside_the_folder_or_our_own_files(string file)
    {
        var manifest = new Manifest { PlaylistId = "PL1" };
        manifest.Tracks["x"] = new TrackRecord { File = file, Status = TrackStatus.RemovedRemote };
        await _store.SaveAsync(_dir.Path, manifest, Ct);

        var result = await Service().DeleteFilesAsync(Entry(), new AppSettings(), ["x"], Ct);

        Assert.Empty(result.Deleted);
        Assert.Single(result.Failed);
        Assert.True(File.Exists(ManifestStore.PathFor(_dir.Path)));
    }

    [Fact]
    public async Task Delete_of_an_already_missing_file_still_forgets_the_track()
    {
        var manifest = new Manifest { PlaylistId = "PL1" };
        manifest.Tracks["r"] = new TrackRecord { File = "r.mp3", Status = TrackStatus.RemovedRemote };
        await _store.SaveAsync(_dir.Path, manifest, Ct);

        var result = await Service().DeleteFilesAsync(Entry(), new AppSettings(), ["r"], Ct);

        Assert.Equal(["r"], result.Deleted);
        Assert.Empty((await SavedManifest())!.Tracks);
    }

    [Fact]
    public async Task Delete_is_refused_while_the_folder_is_syncing()
    {
        _source.Playlist = Remote(T("a", 0));
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _downloader.Before = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var service = Service();

        var sync = service.SyncAsync(Entry(), new AppSettings(), ct: Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var ex = await Assert.ThrowsAsync<SyncException>(() => service.DeleteFilesAsync(Entry(), new AppSettings(), ["a"], Ct));
        Assert.Equal(SyncErrorKind.AlreadyRunning, ex.Kind);

        release.SetResult();
        await sync;
    }

    [Fact]
    public async Task Delete_updates_the_m3u_when_enabled()
    {
        var manifest = new Manifest { PlaylistId = "PL1" };
        manifest.Tracks["u"] = new TrackRecord { Title = "Gone", File = "u.mp3", Status = TrackStatus.Unavailable, RemoteIndex = 0 };
        await _store.SaveAsync(_dir.Path, manifest, Ct);
        await File.WriteAllTextAsync(_dir.File("u.mp3"), "audio", Ct);
        var settings = new AppSettings { Download = new DownloadOptions { WriteM3u = true } };

        await Service().DeleteFilesAsync(Entry(), settings, ["u"], Ct);

        var m3u = await File.ReadAllTextAsync(_dir.File(M3uWriter.FileName(Entry().Name)), Ct);
        Assert.DoesNotContain("u.mp3", m3u);
    }

    // ───── Folder cover ─────

    private const string CustomCover = "https://i.ytimg.com/pl_c/PL1/studio_square_thumbnail.jpg";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private RemotePlaylist RemoteWithCover(string coverUrl) =>
        Remote(T("a", 0)) with { Thumbnails = [new RemoteThumbnail(coverUrl, 544, 544)] };

    [Fact]
    public async Task Saves_the_playlist_cover_into_the_folder_when_enabled()
    {
        _source.Playlist = RemoteWithCover(CustomCover);
        var covers = new FakeCoverStore(_dir.File("cached.img"), Jpeg);

        await Service(covers).SyncAsync(Entry(new DownloadOptions { SaveFolderCover = true }), new AppSettings(), ct: Ct);

        Assert.Equal(Jpeg, await File.ReadAllBytesAsync(_dir.File("cover.jpg"), Ct));
        Assert.Equal([CustomCover], covers.Requested);
    }

    [Fact]
    public async Task Cover_is_off_by_default_and_never_overwrites_an_existing_one()
    {
        _source.Playlist = RemoteWithCover(CustomCover);
        var covers = new FakeCoverStore(_dir.File("cached.img"), Jpeg);

        await Service(covers).SyncAsync(Entry(), new AppSettings(), ct: Ct);
        Assert.False(File.Exists(_dir.File("cover.jpg")));

        await File.WriteAllTextAsync(_dir.File("Folder.JPG"), "mine", Ct);
        await Service(covers).SyncAsync(Entry(new DownloadOptions { SaveFolderCover = true }), new AppSettings(), ct: Ct);
        Assert.False(File.Exists(_dir.File("cover.jpg")));
        Assert.Equal("mine", await File.ReadAllTextAsync(_dir.File("Folder.JPG"), Ct));
        Assert.Empty(covers.Requested);
    }

    [Fact]
    public async Task A_video_thumbnail_fallback_is_not_saved_as_the_folder_cover()
    {
        _source.Playlist = RemoteWithCover("https://i.ytimg.com/vi/a/hqdefault.jpg");
        var covers = new FakeCoverStore(_dir.File("cached.img"), Jpeg);

        await Service(covers).SyncAsync(Entry(new DownloadOptions { SaveFolderCover = true }), new AppSettings(), ct: Ct);

        Assert.False(File.Exists(_dir.File("cover.jpg")));
        Assert.Empty(covers.Requested);
    }

    [Fact]
    public async Task A_failed_cover_download_does_not_fail_the_sync()
    {
        _source.Playlist = RemoteWithCover(CustomCover);
        var covers = new FakeCoverStore(null, null);

        var result = await Service(covers).SyncAsync(Entry(new DownloadOptions { SaveFolderCover = true }), new AppSettings(), ct: Ct);

        Assert.Equal(1, result.Downloaded);
        Assert.False(File.Exists(_dir.File("cover.jpg")));
    }

    private sealed class FakeCoverStore(string? path, byte[]? content) : PlaylistSync.Core.Covers.ICoverStore
    {
        public List<string> Requested { get; } = [];

        public async Task<string?> GetAsync(Guid playlistId, string url, CancellationToken ct = default)
        {
            Requested.Add(url);
            if (path is null || content is null) return null;
            await File.WriteAllBytesAsync(path, content, ct);
            return path;
        }
    }

    private static readonly AppSettings BraveSettings = new()
    {
        Download = new DownloadOptions { CookieMode = CookieMode.Browser, CookiesBrowser = "brave" },
    };

    private BrowserCookieCache CookieCache(FakeProcessRunner runner) =>
        new(runner, _dir.File("cookie-cache"), TimeProvider.System, NullLogger<BrowserCookieCache>.Instance);

    [Fact]
    public async Task Browser_cookies_are_read_once_and_each_process_gets_its_own_file()
    {
        _source.Playlist = Remote(T("a", 0), T("b", 1), T("c", 2));
        var runner = BrowserCookieCacheTests.ExportingRunner();
        var seen = new System.Collections.Concurrent.ConcurrentBag<(string Path, bool Existed)>();
        _downloader.Before = (r, _) =>
        {
            Assert.Equal(CookieMode.File, r.Options.CookieMode);
            seen.Add((r.Options.CookiesFile!, File.Exists(r.Options.CookiesFile)));
            return Task.CompletedTask;
        };
        var events = new EventSink();
        var service = Service(cookies: CookieCache(runner));

        var result = await service.SyncAsync(Entry(), BraveSettings, events, ct: Ct);
        await service.SyncAsync(Entry(), BraveSettings, ct: Ct); // reuses the snapshot

        Assert.Equal(3, result.Downloaded);
        Assert.Single(runner.Requests);
        Assert.Equal(SyncStage.ReadingCookies, events.Of<SyncStageChanged>().First().Stage);
        Assert.Equal(CookieMode.File, _source.LastContext!.Options.CookieMode);
        Assert.True(_source.CookieFileExisted);
        Assert.Equal(3, seen.Count);
        Assert.All(seen, s => Assert.True(s.Existed));
        Assert.Equal(3, seen.Select(s => s.Path).Distinct().Count());
        // Every copy is deleted once its process is done.
        Assert.Empty(Directory.EnumerateFiles(_dir.File("cookie-cache")));
    }

    [Fact]
    public async Task Sign_in_errors_drop_the_cached_cookies()
    {
        _source.Playlist = Remote(T("a", 0));
        var runner = BrowserCookieCacheTests.ExportingRunner();
        _downloader.Respond = _ => DownloadResult.Failure(DownloadErrorKind.NeedsCookies, "Sign in to confirm you're not a bot");
        var service = Service(cookies: CookieCache(runner));

        await service.SyncAsync(Entry(), BraveSettings, ct: Ct);
        await service.SyncAsync(Entry(), BraveSettings, ct: Ct);

        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public async Task Failed_cookie_export_falls_back_to_reading_the_browser_per_process()
    {
        _source.Playlist = Remote(T("a", 0));
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Fail("ERROR: keyring locked"));

        await Service(cookies: CookieCache(runner)).SyncAsync(Entry(), BraveSettings, ct: Ct);

        Assert.Equal(CookieMode.Browser, _downloader.Calls.Single().Options.CookieMode);
        Assert.Equal(CookieMode.Browser, _source.LastContext!.Options.CookieMode);
    }

    private sealed class FakeSource : IPlaylistSource
    {
        public RemotePlaylist? Playlist { get; set; }
        public PlaylistFetchException? Error { get; set; }
        public SourceContext? LastContext { get; private set; }
        public bool CookieFileExisted { get; private set; }

        public bool CanHandle(string url) => true;

        public Task<RemotePlaylist> FetchAsync(string url, SourceContext context, CancellationToken ct = default)
        {
            LastContext = context;
            CookieFileExisted = context.Options.CookiesFile is { } f && File.Exists(f);
            return Error is not null ? Task.FromException<RemotePlaylist>(Error) : Task.FromResult(Playlist!);
        }
    }

    /// <summary>Writes <c>&lt;id&gt;.mp3</c> into the target folder unless <see cref="Respond"/> returns a result.</summary>
    private sealed class FakeDownloader : ITrackDownloader
    {
        private readonly List<DownloadRequest> _calls = [];

        public IReadOnlyList<DownloadRequest> Calls
        {
            get { lock (_calls) return _calls.ToList(); }
        }

        public Func<DownloadRequest, DownloadResult?> Respond { get; set; } = _ => null;
        public Func<DownloadRequest, CancellationToken, Task>? Before { get; set; }

        public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            lock (_calls) _calls.Add(request);
            if (Before is not null) await Before(request, ct);
            ct.ThrowIfCancellationRequested();
            if (Respond(request) is { } result) return result;

            progress?.Report(new DownloadProgress(DownloadPhase.Downloading, 100));
            var file = request.VideoId + ".mp3";
            await File.WriteAllTextAsync(Path.Combine(request.TargetDirectory, file), "audio", ct);
            return DownloadResult.Success(file);
        }
    }

    // ---- replacements for gone videos --------------------------------------------------------

    private void OriginalIsGone(string id = "gone") =>
        _downloader.Respond = r => r.VideoId == id && r.SourceVideoId is null
            ? DownloadResult.Failure(DownloadErrorKind.Unavailable, "Video unavailable. This video is not available")
            : null;

    [Fact]
    public async Task Gone_video_is_downloaded_from_a_matching_replacement()
    {
        _source.Playlist = Remote(T("gone", 0));
        OriginalIsGone();
        _finder.Respond = _ => new ReplacementResult(ReplacementOutcome.Found, new Replacement("new", "Title gone", "Artist", 180));
        var events = new EventSink();

        var result = await Service().SyncAsync(Entry(), new AppSettings(), events, ct: Ct);

        Assert.Equal((1, 0, 0), (result.Downloaded, result.Failed, result.BecameUnavailable));
        var query = Assert.Single(_finder.Calls);
        Assert.Equal(new ReplacementQuery("gone", "Title gone", "Artist", 180), query);
        Assert.Equal("new", _downloader.Calls[^1].SourceVideoId);
        Assert.Equal("gone", _downloader.Calls[^1].VideoId);

        var saved = (await SavedManifest())!.Tracks["gone"];
        Assert.Equal(TrackStatus.Synced, saved.Status);
        Assert.Equal("new", saved.ReplacementId);
        Assert.Null(saved.LastError);
        Assert.Contains(events.Of<TrackProgress>(), e => e.Progress.Phase == DownloadPhase.SearchingReplacement);
    }

    [Fact]
    public async Task Known_replacement_is_reused_without_searching()
    {
        _source.Playlist = Remote(T("gone", 0));
        OriginalIsGone();
        _finder.Respond = _ => new ReplacementResult(ReplacementOutcome.Found, new Replacement("new", "T", "A", 180));
        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);
        File.Delete(_dir.File("gone.mp3"));

        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);

        Assert.Single(_finder.Calls);
        Assert.Equal(TrackStatus.Synced, (await SavedManifest())!.Tracks["gone"].Status);
        Assert.Equal("new", _downloader.Calls[^1].SourceVideoId);
    }

    [Fact]
    public async Task Without_a_match_the_track_stays_unavailable_and_is_not_searched_again_soon()
    {
        _source.Playlist = Remote(T("gone", 0));
        OriginalIsGone();

        var result = await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);

        Assert.Equal(1, result.BecameUnavailable);
        var saved = (await SavedManifest())!.Tracks["gone"];
        Assert.Equal(TrackStatus.Unavailable, saved.Status);
        Assert.Null(saved.ReplacementId);
        Assert.NotNull(saved.ReplacementSearchedAt);
        Assert.Contains("No matching upload", saved.LastError);

        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);
        Assert.Single(_finder.Calls);
        Assert.Equal(TrackStatus.Unavailable, (await SavedManifest())!.Tracks["gone"].Status);
    }

    [Fact]
    public async Task Failed_search_is_retried_on_the_next_sync()
    {
        _source.Playlist = Remote(T("gone", 0));
        OriginalIsGone();
        _finder.Respond = _ => new ReplacementResult(ReplacementOutcome.Error, Error: "bot check");

        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);
        Assert.Null((await SavedManifest())!.Tracks["gone"].ReplacementSearchedAt);

        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);
        Assert.Equal(2, _finder.Calls.Count);
    }

    [Fact]
    public async Task Replacements_can_be_turned_off()
    {
        _source.Playlist = Remote(T("gone", 0));
        OriginalIsGone();
        _finder.Respond = _ => new ReplacementResult(ReplacementOutcome.Found, new Replacement("new", "T", "A", 180));
        var settings = new AppSettings { Download = new DownloadOptions { FindReplacements = false } };

        await Service().SyncAsync(Entry(), settings, ct: Ct);

        Assert.Empty(_finder.Calls);
        Assert.Equal(TrackStatus.Unavailable, (await SavedManifest())!.Tracks["gone"].Status);
    }

    [Fact]
    public async Task Only_gone_videos_are_replaced()
    {
        _source.Playlist = Remote(T("bad", 0), T("unknown-length", 1) with { DurationSec = null });
        _downloader.Respond = r => r.VideoId switch
        {
            "bad" => DownloadResult.Failure(DownloadErrorKind.Other, "HTTP Error 403"),
            _ => DownloadResult.Failure(DownloadErrorKind.Unavailable, "Video unavailable"),
        };

        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);

        // A failure is retried as is; a gone track without a known length can't be matched.
        Assert.Empty(_finder.Calls);
    }

    [Fact]
    public async Task Gone_replacement_is_forgotten_so_the_next_sync_searches_again()
    {
        _source.Playlist = Remote(T("gone", 0));
        _downloader.Respond = _ => DownloadResult.Failure(DownloadErrorKind.Unavailable, "Video unavailable");
        _finder.Respond = _ => new ReplacementResult(ReplacementOutcome.Found, new Replacement("new", "T", "A", 180));

        await Service().SyncAsync(Entry(), new AppSettings(), ct: Ct);

        var saved = (await SavedManifest())!.Tracks["gone"];
        Assert.Equal(TrackStatus.Unavailable, saved.Status);
        Assert.Null(saved.ReplacementId);
        Assert.Contains("Replacement new: Video unavailable", saved.LastError);
    }

    private sealed class FakeFinder : IReplacementFinder
    {
        private readonly List<ReplacementQuery> _calls = [];

        public IReadOnlyList<ReplacementQuery> Calls
        {
            get { lock (_calls) return _calls.ToList(); }
        }

        public Func<ReplacementQuery, ReplacementResult> Respond { get; set; } = _ => ReplacementResult.NotFound;

        public Task<ReplacementResult> FindAsync(ReplacementQuery query, DownloadOptions options, DownloadTools tools, CancellationToken ct)
        {
            lock (_calls) _calls.Add(query);
            return Task.FromResult(Respond(query));
        }
    }

    private sealed class EventSink : IProgress<SyncEvent>
    {
        private readonly List<SyncEvent> _events = [];

        public void Report(SyncEvent value)
        {
            lock (_events) _events.Add(value);
        }

        public IReadOnlyList<T> Of<T>() where T : SyncEvent
        {
            lock (_events) return _events.OfType<T>().ToList();
        }
    }
}

public class M3uWriterTests
{
    [Theory]
    [InlineData("Chill Mix", "Chill Mix.m3u8")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j.m3u8")]
    [InlineData("  ...  ", "playlist.m3u8")]
    [InlineData("Trailing dot.", "Trailing dot.m3u8")]
    public void File_name_is_portable(string name, string expected)
    {
        Assert.Equal(expected, M3uWriter.FileName(name));
    }

    [Fact]
    public void Lists_playable_tracks_in_remote_order()
    {
        var m = new Manifest();
        m.Tracks["late"] = new TrackRecord { Title = "Late", Uploader = "X", File = "Sub/late.mp3", Status = TrackStatus.Synced, RemoteIndex = 5, DurationSec = 61.6 };
        m.Tracks["early"] = new TrackRecord { Title = "Early", File = "early.mp3", Status = TrackStatus.Synced, RemoteIndex = 0 };
        m.Tracks["kept"] = new TrackRecord { Title = "Kept", File = "kept.mp3", Status = TrackStatus.Unavailable, RemoteIndex = 2 };
        m.Tracks["removed"] = new TrackRecord { Title = "Removed", File = "removed.mp3", Status = TrackStatus.RemovedRemote, RemoteIndex = 1 };
        m.Tracks["missing"] = new TrackRecord { Title = "Missing", File = "missing.mp3", Status = TrackStatus.MissingLocal, RemoteIndex = 3 };
        m.Tracks["pending"] = new TrackRecord { Title = "Pending", Status = TrackStatus.Pending, RemoteIndex = 4 };

        var sep = Path.DirectorySeparatorChar;
        Assert.Equal(
            $"#EXTM3U\n#EXTINF:-1,Early\nearly.mp3\n#EXTINF:-1,Kept\nkept.mp3\n#EXTINF:62,Late\nSub{sep}late.mp3\n",
            M3uWriter.Build(m));
    }
}
