using Microsoft.Extensions.Logging.Abstractions;
using PlaylistSync.Core.Covers;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playlists;
using PlaylistSync.Core.Settings;
using PlaylistSync.Core.Sources;
using PlaylistSync.Core.Storage;
using PlaylistSync.Core.Sync;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

/// <summary>Video playlists (<see cref="PlaylistKind.Video"/>), and the sidebar order / hidden flag of the library.</summary>
public sealed class VideoPlaylistTests : IDisposable
{
    private const string Temp = "/videos/.playlist-sync-tmp/vid123";
    private static readonly ToolLocation Ffmpeg = new(ToolKind.Ffmpeg, "/usr/bin/ffmpeg", "ffmpeg", ToolSource.SystemPath);
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<string> Video(DownloadOptions? o = null, bool keepThumbnail = false) =>
        YtDlpArgsBuilder.TrackDownload("vid123", Temp, o ?? new DownloadOptions(), Ffmpeg, null, null, 2, isWindows: false,
            keepThumbnail, PlaylistKind.Video);

    private static string? ValueAfter(IReadOnlyList<string> args, string option)
    {
        var i = args.ToList().IndexOf(option);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    // ---- download arguments --------------------------------------------------------------------

    [Fact]
    public void Video_download_merges_best_video_and_audio_up_to_1080p_into_mp4()
    {
        var args = Video();

        Assert.Equal("bv*+ba/b", ValueAfter(args, "-f"));
        Assert.Equal("res:1080,ext:mp4:m4a", ValueAfter(args, "-S"));
        Assert.Equal("mp4/mkv", ValueAfter(args, "--merge-output-format"));
        Assert.DoesNotContain("-x", args);
        Assert.DoesNotContain("--audio-format", args);
        Assert.Equal(DownloadOptions.DefaultVideoFilenameTemplate, ValueAfter(args, "-o"));
        Assert.Equal("--", args[^2]);
        Assert.Equal("https://www.youtube.com/watch?v=vid123", args[^1]);
    }

    [Fact]
    public void Video_download_leaves_out_the_music_only_options()
    {
        var args = Video(new DownloadOptions { ParseArtistTitle = true, CropThumbnailSquare = true, SponsorBlockRemoveOfftopic = true });

        Assert.DoesNotContain("--parse-metadata", args);
        Assert.DoesNotContain("--ppa", args);
        Assert.DoesNotContain("--sponsorblock-remove", args);
        Assert.Contains("--embed-thumbnail", args); // the 16:9 thumbnail, uncropped
        Assert.Contains("--embed-metadata", args);
    }

    [Fact]
    public void Video_download_honours_resolution_container_subtitles_and_template()
    {
        var args = Video(new DownloadOptions
        {
            VideoMaxHeight = null,
            VideoContainer = VideoContainer.Mkv,
            EmbedSubtitles = true,
            SubtitleLanguages = " en.*,de ",
            VideoFilenameTemplate = "%(uploader)s/%(title)s.%(ext)s",
            TrackNumberPrefix = true,
        });

        Assert.Null(ValueAfter(args, "-S")); // best available, no container preference
        Assert.Equal("mkv", ValueAfter(args, "--merge-output-format"));
        Assert.Contains("--embed-subs", args);
        Assert.Equal("en.*,de", ValueAfter(args, "--sub-langs"));
        Assert.Equal("%(uploader)s/%(title)s.%(ext)s", ValueAfter(args, "-o"));

        var numbered = YtDlpArgsBuilder.TrackDownload("vid123", Temp, new DownloadOptions { TrackNumberPrefix = true }, Ffmpeg, null,
            7, 3, isWindows: false, kind: PlaylistKind.Video);
        Assert.Equal("007 - %(title)s.%(ext)s", ValueAfter(numbered, "-o"));
    }

    [Fact]
    public void Music_download_ignores_the_video_options()
    {
        var args = YtDlpArgsBuilder.TrackDownload("vid123", Temp,
            new DownloadOptions { EmbedSubtitles = true, VideoContainer = VideoContainer.Mkv }, Ffmpeg, null);

        Assert.Equal("bestaudio/best", ValueAfter(args, "-f"));
        Assert.Contains("-x", args);
        Assert.DoesNotContain("--embed-subs", args);
        Assert.DoesNotContain("--merge-output-format", args);
        Assert.Equal(DownloadOptions.DefaultFilenameTemplate, ValueAfter(args, "-o"));
    }

    [Fact]
    public void Toolbox_video_download_is_unchanged()
    {
        var spec = new SingleDownloadSpec("https://youtu.be/x", "/out", "/tmp/t", SingleDownloadKind.Video, AudioFormat.Mp3, 720,
            new DownloadOptions());
        var args = YtDlpArgsBuilder.SingleDownload(spec, Ffmpeg, null, isWindows: false);

        Assert.Equal("bv*+ba/b", ValueAfter(args, "-f"));
        Assert.Equal("res:720,ext:mp4:m4a", ValueAfter(args, "-S"));
        Assert.Equal("mp4/mkv", ValueAfter(args, "--merge-output-format"));
    }

    // ---- settings ------------------------------------------------------------------------------

    [Fact]
    public void Video_settings_are_validated()
    {
        static SettingsValidation V(DownloadOptions o) => SettingsValidator.ValidateDownload(o, _ => true);

        Assert.True(V(new DownloadOptions()).IsValid);
        Assert.True(V(new DownloadOptions { VideoMaxHeight = null, EmbedSubtitles = true, SubtitleLanguages = "all,-live_chat" }).IsValid);
        Assert.NotNull(V(new DownloadOptions { VideoFilenameTemplate = "/abs/%(title)s.%(ext)s" }).Error(nameof(DownloadOptions.VideoFilenameTemplate)));
        Assert.NotNull(V(new DownloadOptions { VideoMaxHeight = 50 }).Error(nameof(DownloadOptions.VideoMaxHeight)));
        Assert.NotNull(V(new DownloadOptions { EmbedSubtitles = true, SubtitleLanguages = "en; rm -rf" }).Error(nameof(DownloadOptions.SubtitleLanguages)));
        // Not checked while subtitles are off.
        Assert.True(V(new DownloadOptions { EmbedSubtitles = false, SubtitleLanguages = "" }).IsValid);

        Assert.NotNull(SettingsValidator.ValidateApp(new AppSettings { DefaultVideoDirectory = "relative/dir" })
            .Error(nameof(AppSettings.DefaultVideoDirectory)));
        Assert.True(SettingsValidator.ValidateApp(new AppSettings { DefaultVideoDirectory = "~/Videos/YT" }, "/home/u").IsValid);
    }

    [Fact]
    public async Task Old_library_and_settings_files_load_as_music_and_visible()
    {
        var paths = new AppPaths(_dir.File("cfg"), _dir.File("data"));
        Directory.CreateDirectory(paths.ConfigDir);
        await File.WriteAllTextAsync(paths.LibraryFile,
            """{ "version": 1, "playlists": [ { "id": "6f1c1a7e-0000-4000-8000-000000000001", "name": "Mix", "url": "u", "directory": "/m" } ] }""",
            Ct);
        var store = new ConfigStore(paths, NullLogger<ConfigStore>.Instance);

        var entry = Assert.Single((await store.LoadLibraryAsync(Ct)).Playlists);
        Assert.Equal(PlaylistKind.Music, entry.Kind);
        Assert.False(entry.Hidden);

        await store.SaveLibraryAsync(new Library { Playlists = [entry with { Kind = PlaylistKind.Video, Hidden = true }] }, Ct);
        var json = await File.ReadAllTextAsync(paths.LibraryFile, Ct);
        Assert.Contains("\"video\"", json);
        var reloaded = Assert.Single((await store.LoadLibraryAsync(Ct)).Playlists);
        Assert.Equal((PlaylistKind.Video, true), (reloaded.Kind, reloaded.Hidden));
    }

    // ---- planner -------------------------------------------------------------------------------

    [Fact]
    public void Untracked_files_are_the_media_files_of_the_playlist_kind()
    {
        var disk = new DiskSnapshot(["clip.mp4", "movie.mkv", "song.mp3", "both.webm", "cover.jpg"], StringComparer.Ordinal);

        Assert.Equal(["both.webm", "clip.mp4", "movie.mkv"], SyncPlanner.FindUntracked(null, disk, PlaylistKind.Video));
        Assert.Equal(["both.webm", "song.mp3"], SyncPlanner.FindUntracked(null, disk));
    }

    [Fact]
    public void Video_playlist_prefers_a_wide_cover()
    {
        RemoteThumbnail[] thumbs =
        [
            new("https://i.ytimg.com/vi/a/square.jpg", 720, 720),
            new("https://i.ytimg.com/vi/a/wide.jpg", 480, 270),
        ];

        Assert.Equal("https://i.ytimg.com/vi/a/square.jpg", PlaylistCover.Pick(thumbs));
        Assert.Equal("https://i.ytimg.com/vi/a/wide.jpg", PlaylistCover.Pick(thumbs, preferWide: true));
        // A custom cover still wins.
        Assert.Equal("https://i.ytimg.com/pl_c/PL/c.jpg",
            PlaylistCover.Pick([.. thumbs, new("https://i.ytimg.com/pl_c/PL/c.jpg", 300, 300)], preferWide: true));

        var remote = new RemotePlaylist("PL", "Clips", "me", "u", []) { Thumbnails = thumbs };
        var plan = SyncPlanner.Plan(remote, null, DiskSnapshot.Empty, "u", true, DateTimeOffset.UnixEpoch, kind: PlaylistKind.Video);
        Assert.Equal("https://i.ytimg.com/vi/a/wide.jpg", plan.Manifest.CoverUrl);
    }

    // ---- thumbnails ----------------------------------------------------------------------------

    [Fact]
    public void Wide_thumbnails_are_16_by_9_and_stored_next_to_the_square_ones()
    {
        var args = TrackThumbnails.FfmpegArgs("/t/thumbnail.webp", "/d/id.wide.jpg.tmp", shape: ThumbnailShape.Wide);
        var filter = args[args.ToList().IndexOf("-vf") + 1];
        Assert.StartsWith("crop='min(iw,ih*16/9)':'min(ih,iw*9/16)'", filter);
        Assert.Contains($"scale={TrackThumbnails.WideWidth}:{TrackThumbnails.Size}", filter);

        var store = new TrackThumbnails(new AppPaths(_dir.File("cfg"), _dir.File("data")), new FakeProcessRunner(_ => FakeProcessRunner.Ok("")),
            NullLogger<TrackThumbnails>.Instance);
        Assert.EndsWith("abc.jpg", store.PathFor("abc"));
        Assert.EndsWith("abc.wide.jpg", store.PathFor("abc", ThumbnailShape.Wide));
        Assert.NotEqual(store.PathFor("abc"), store.PathFor("abc", ThumbnailShape.Wide));
        Assert.Equal(ThumbnailShape.Wide, TrackThumbnails.ShapeFor(PlaylistKind.Video));
        Assert.Equal(ThumbnailShape.Square, TrackThumbnails.ShapeFor(PlaylistKind.Music));
    }

    // ---- library order -------------------------------------------------------------------------

    private static PlaylistEntry E(string name, PlaylistKind kind = PlaylistKind.Music, bool hidden = false) =>
        new() { Name = name, Url = "u" + name, Directory = "/d/" + name, Kind = kind, Hidden = hidden };

    [Fact]
    public void Reordering_a_subset_keeps_the_other_entries_in_their_slots()
    {
        var m1 = E("m1");
        var v1 = E("v1", PlaylistKind.Video);
        var m2 = E("m2");
        var h = E("h", hidden: true);
        var m3 = E("m3");
        List<PlaylistEntry> all = [m1, v1, m2, h, m3];

        // The music sidebar without hidden ones shows m1, m2, m3; the user drags m3 to the top.
        var result = LibraryOrder.Apply(all, [m3.Id, m1.Id, m2.Id]);

        Assert.Equal(["m3", "v1", "m1", "h", "m2"], result.Select(e => e.Name));
        // Unknown and repeated ids are ignored; the input is not changed.
        Assert.Equal(["m2", "v1", "m1", "h", "m3"], LibraryOrder.Apply(all, [m2.Id, Guid.NewGuid(), m2.Id, m1.Id, m3.Id]).Select(e => e.Name));
        Assert.Equal(["m1", "v1", "m2", "h", "m3"], all.Select(e => e.Name));
    }

    [Fact]
    public async Task Reorder_and_hidden_flag_are_saved()
    {
        var store = new ConfigStore(new AppPaths(_dir.File("cfg"), _dir.File("data")), NullLogger<ConfigStore>.Instance);
        var lib = new PlaylistLibrary(store, NullLogger<PlaylistLibrary>.Instance);
        await lib.LoadAsync(Ct);
        var a = E("a");
        var v = E("v", PlaylistKind.Video);
        var b = E("b");
        await lib.AddAsync(a, Ct);
        await lib.AddAsync(v, Ct);
        await lib.AddAsync(b, Ct);

        await lib.ReorderAsync([b.Id, a.Id], Ct);
        await lib.UpdateAsync(a.Id, e => e with { Hidden = true }, Ct);

        var reloaded = new PlaylistLibrary(store, NullLogger<PlaylistLibrary>.Instance);
        await reloaded.LoadAsync(Ct);
        Assert.Equal(["b", "v", "a"], reloaded.Entries.Select(e => e.Name));
        Assert.True(reloaded.Find(a.Id)!.Hidden);
        Assert.Equal(PlaylistKind.Video, reloaded.Find(v.Id)!.Kind);
    }

    [Fact]
    public void Default_folders_follow_the_kind()
    {
        Assert.Equal(PathUtil.MusicDir, PathUtil.DefaultDirFor(PlaylistKind.Music));
        Assert.Equal(PathUtil.VideosDir, PathUtil.DefaultDirFor(PlaylistKind.Video));
        Assert.NotEqual(PathUtil.MusicDir, PathUtil.VideosDir);
    }
}
