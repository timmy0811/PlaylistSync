using PlaylistSync.Core.Models;
using PlaylistSync.Core.Sources;
using PlaylistSync.Core.Sync;

namespace PlaylistSync.Core.Tests;

public class SyncPlannerTests
{
    private const string Url = "https://www.youtube.com/playlist?list=PL1";
    private static readonly DateTimeOffset Then = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    // ---- first sync -------------------------------------------------------------------------

    [Fact]
    public void First_sync_queues_everything_in_remote_order()
    {
        var plan = Plan(Remote(T("b"), T("a"), T("c")), null);

        Assert.Equal(["b", "a", "c"], plan.ToDownload);
        Assert.All(plan.Manifest.Tracks.Values, r => Assert.Equal(TrackStatus.Pending, r.Status));
        Assert.All(plan.Changes, c => Assert.Null(c.From));
        Assert.Equal(3, plan.Changes.Count);

        var b = plan.Manifest.Tracks["b"];
        Assert.Equal("Title b", b.Title);
        Assert.Equal("Uploader b", b.Uploader);
        Assert.Equal(180, b.DurationSec);
        Assert.Equal(0, b.RemoteIndex);
        Assert.Equal(Now, b.FirstSeen);
        Assert.Equal(Now, b.LastSeenRemote);
        Assert.Null(b.File);
    }

    [Fact]
    public void First_sync_fills_manifest_header()
    {
        var plan = Plan(Remote(T("a")), null);

        Assert.Equal(Url, plan.Manifest.PlaylistUrl);
        Assert.Equal("PL1", plan.Manifest.PlaylistId);
        Assert.Equal("My Mix", plan.Manifest.PlaylistTitle);
        Assert.Equal(Manifest.CurrentVersion, plan.Manifest.Version);
        Assert.False(plan.IsDifferentPlaylist);
    }

    [Fact]
    public void Empty_remote_and_no_manifest_is_a_noop()
    {
        var plan = Plan(Remote(), null);
        Assert.Empty(plan.ToDownload);
        Assert.Empty(plan.Changes);
        Assert.Empty(plan.Manifest.Tracks);
    }

    // ---- steady state ----------------------------------------------------------------------

    [Fact]
    public void Synced_track_with_file_stays_synced_and_is_not_downloaded()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "a.mp3")));
        var plan = Plan(Remote(T("a")), manifest, Disk("a.mp3"));

        Assert.Empty(plan.ToDownload);
        Assert.Empty(plan.Changes);
        Assert.Equal(TrackStatus.Synced, plan.Manifest.Tracks["a"].Status);
        Assert.Equal(Now, plan.Manifest.Tracks["a"].LastSeenRemote);
        Assert.Equal(Then, plan.Manifest.Tracks["a"].FirstSeen);
    }

    [Fact]
    public void New_remote_track_is_added_next_to_existing_ones()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "a.mp3")));
        var plan = Plan(Remote(T("a"), T("new")), manifest, Disk("a.mp3"));

        Assert.Equal(["new"], plan.ToDownload);
        Assert.Equal(new TrackChange("new", "Title new", null, TrackStatus.Pending), plan.Changes.Single());
    }

    [Fact]
    public void Remote_metadata_and_position_are_refreshed_but_file_is_kept()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "Old Name.mp3", title: "Old title", index: 5)));
        var plan = Plan(Remote(T("x"), T("a", title: "New title")), manifest, Disk("Old Name.mp3"));

        var a = plan.Manifest.Tracks["a"];
        Assert.Equal("New title", a.Title);
        Assert.Equal("Old Name.mp3", a.File);
        Assert.Equal(1, a.RemoteIndex);
        Assert.DoesNotContain("a", plan.ToDownload);
    }

    [Fact]
    public void Missing_remote_metadata_does_not_erase_known_values()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "a.mp3", title: "Known")));
        var plan = Plan(Remote(new RemoteTrack("a", null, null, null, 0)), manifest, Disk("a.mp3"));

        var a = plan.Manifest.Tracks["a"];
        Assert.Equal("Known", a.Title);
        Assert.Equal("Uploader", a.Uploader);
        Assert.Equal(100, a.DurationSec);
    }

    // ---- removed remotely ------------------------------------------------------------------

    [Fact]
    public void Removed_remote_track_is_highlighted_and_file_kept()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "a.mp3")), ("gone", Rec(TrackStatus.Synced, "gone.mp3")));
        var plan = Plan(Remote(T("a")), manifest, Disk("a.mp3", "gone.mp3"));

        var gone = plan.Manifest.Tracks["gone"];
        Assert.Equal(TrackStatus.RemovedRemote, gone.Status);
        Assert.Equal("gone.mp3", gone.File);
        Assert.Equal(Then, gone.LastSeenRemote);
        Assert.Empty(plan.ToDownload);
        Assert.Equal(new TrackChange("gone", "Title", TrackStatus.Synced, TrackStatus.RemovedRemote), plan.Changes.Single());
        Assert.Empty(plan.Untracked);
    }

    [Fact]
    public void Already_removed_track_reports_no_change()
    {
        var manifest = M(("gone", Rec(TrackStatus.RemovedRemote, "gone.mp3")));
        var plan = Plan(Remote(), manifest, Disk("gone.mp3"));

        Assert.Equal(TrackStatus.RemovedRemote, plan.Manifest.Tracks["gone"].Status);
        Assert.Empty(plan.Changes);
    }

    [Theory]
    [InlineData(TrackStatus.Synced)]
    [InlineData(TrackStatus.MissingLocal)]
    [InlineData(TrackStatus.Unavailable)]
    [InlineData(TrackStatus.Failed)]
    public void Removed_track_with_recorded_file_is_kept_even_if_file_is_gone(TrackStatus status)
    {
        var manifest = M(("gone", Rec(status, "gone.mp3")));
        var plan = Plan(Remote(), manifest);

        Assert.Equal(TrackStatus.RemovedRemote, plan.Manifest.Tracks["gone"].Status);
    }

    [Theory]
    [InlineData(TrackStatus.Pending)]
    [InlineData(TrackStatus.Failed)]
    [InlineData(TrackStatus.Unavailable)]
    [InlineData(TrackStatus.Downloading)]
    public void Removed_track_that_was_never_downloaded_is_forgotten(TrackStatus status)
    {
        var manifest = M(("never", Rec(status, file: null)));
        var plan = Plan(Remote(), manifest);

        Assert.False(plan.Manifest.Tracks.ContainsKey("never"));
        Assert.Equal(new TrackChange("never", "Title", status, null), plan.Changes.Single());
    }

    [Fact]
    public void Re_added_track_with_file_goes_back_to_synced()
    {
        var manifest = M(("a", Rec(TrackStatus.RemovedRemote, "a.mp3")));
        var plan = Plan(Remote(T("a")), manifest, Disk("a.mp3"));

        Assert.Equal(TrackStatus.Synced, plan.Manifest.Tracks["a"].Status);
        Assert.Empty(plan.ToDownload);
        Assert.Equal(new TrackChange("a", "Title a", TrackStatus.RemovedRemote, TrackStatus.Synced), plan.Changes.Single());
    }

    [Fact]
    public void Re_added_track_whose_file_was_deleted_is_downloaded_again()
    {
        var manifest = M(("a", Rec(TrackStatus.RemovedRemote, "a.mp3")));
        var plan = Plan(Remote(T("a")), manifest);

        Assert.Equal(["a"], plan.ToDownload);
    }

    // ---- missing local files ---------------------------------------------------------------

    [Fact]
    public void Deleted_local_file_is_redownloaded_by_default()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "a.mp3")));
        var plan = Plan(Remote(T("a")), manifest);

        Assert.Equal(["a"], plan.ToDownload);
        Assert.Equal(TrackStatus.Pending, plan.Manifest.Tracks["a"].Status);
    }

    [Fact]
    public void Deleted_local_file_becomes_missing_when_redownload_is_off()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "a.mp3")));
        var plan = Plan(Remote(T("a")), manifest, redownloadMissing: false);

        Assert.Empty(plan.ToDownload);
        Assert.Equal(TrackStatus.MissingLocal, plan.Manifest.Tracks["a"].Status);
        Assert.Equal("a.mp3", plan.Manifest.Tracks["a"].File);
    }

    [Fact]
    public void Missing_track_whose_file_reappeared_is_synced_again()
    {
        var manifest = M(("a", Rec(TrackStatus.MissingLocal, "a.mp3")));
        var plan = Plan(Remote(T("a")), manifest, Disk("a.mp3"), redownloadMissing: false);

        Assert.Equal(TrackStatus.Synced, plan.Manifest.Tracks["a"].Status);
    }

    [Fact]
    public void Never_downloaded_track_is_queued_even_when_redownload_is_off()
    {
        var manifest = M(("a", Rec(TrackStatus.Pending, file: null)));
        var plan = Plan(Remote(T("a")), manifest, redownloadMissing: false);

        Assert.Equal(["a"], plan.ToDownload);
    }

    // ---- failures / interrupted runs -------------------------------------------------------

    [Fact]
    public void Failed_track_is_retried_and_keeps_its_error_until_it_succeeds()
    {
        var manifest = M(("a", Rec(TrackStatus.Failed, file: null, error: "HTTP 403")));
        var plan = Plan(Remote(T("a")), manifest);

        Assert.Equal(["a"], plan.ToDownload);
        Assert.Equal(TrackStatus.Pending, plan.Manifest.Tracks["a"].Status);
        Assert.Equal("HTTP 403", plan.Manifest.Tracks["a"].LastError);
    }

    [Fact]
    public void Stale_downloading_status_from_a_crash_is_requeued()
    {
        var manifest = M(("a", Rec(TrackStatus.Downloading, file: null)));
        var plan = Plan(Remote(T("a")), manifest);

        Assert.Equal(["a"], plan.ToDownload);
        Assert.Equal(TrackStatus.Pending, plan.Manifest.Tracks["a"].Status);
    }

    [Fact]
    public void Synced_track_clears_old_error()
    {
        var manifest = M(("a", Rec(TrackStatus.Failed, "a.mp3", error: "boom")));
        var plan = Plan(Remote(T("a")), manifest, Disk("a.mp3"));

        Assert.Equal(TrackStatus.Synced, plan.Manifest.Tracks["a"].Status);
        Assert.Null(plan.Manifest.Tracks["a"].LastError);
    }

    // ---- unavailable -----------------------------------------------------------------------

    [Fact]
    public void New_unavailable_track_is_recorded_but_not_downloaded()
    {
        var plan = Plan(Remote(T("p", unavailable: "private video", title: "[Private video]")), null);

        Assert.Empty(plan.ToDownload);
        Assert.Equal(TrackStatus.Unavailable, plan.Manifest.Tracks["p"].Status);
        Assert.Equal("[Private video]", plan.Manifest.Tracks["p"].Title);
    }

    [Fact]
    public void Synced_track_turning_private_is_unavailable_not_removed_and_keeps_metadata()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "a.mp3", title: "Real title")));
        var plan = Plan(Remote(new RemoteTrack("a", "[Private video]", null, null, 0, "private video")), manifest, Disk("a.mp3"));

        var a = plan.Manifest.Tracks["a"];
        Assert.Equal(TrackStatus.Unavailable, a.Status);
        Assert.Equal("Real title", a.Title);
        Assert.Equal("a.mp3", a.File);
        Assert.Equal(Now, a.LastSeenRemote);
        Assert.Empty(plan.ToDownload);
    }

    [Fact]
    public void Unavailable_track_that_becomes_available_is_downloaded()
    {
        var manifest = M(("a", Rec(TrackStatus.Unavailable, file: null)));
        var plan = Plan(Remote(T("a")), manifest);

        Assert.Equal(["a"], plan.ToDownload);
    }

    [Fact]
    public void Unavailable_track_that_becomes_available_with_file_on_disk_is_synced()
    {
        var manifest = M(("a", Rec(TrackStatus.Unavailable, "a.mp3")));
        var plan = Plan(Remote(T("a")), manifest, Disk("a.mp3"));

        Assert.Equal(TrackStatus.Synced, plan.Manifest.Tracks["a"].Status);
        Assert.Empty(plan.ToDownload);
    }

    [Fact]
    public void Gone_track_with_a_replacement_file_stays_synced()
    {
        var record = Rec(TrackStatus.Synced, "a.mp3");
        record.ReplacementId = "new";
        var plan = Plan(Remote(T("a", unavailable: "deleted video", title: "[Deleted video]")), M(("a", record)), Disk("a.mp3"));

        Assert.Equal(TrackStatus.Synced, plan.Manifest.Tracks["a"].Status);
        Assert.Equal("new", plan.Manifest.Tracks["a"].ReplacementId);
        Assert.Empty(plan.ToDownload);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void Gone_track_whose_replacement_file_was_deleted_is_unavailable()
    {
        var record = Rec(TrackStatus.Synced, "a.mp3");
        record.ReplacementId = "new";
        var plan = Plan(Remote(T("a", unavailable: "deleted video", title: "[Deleted video]")), M(("a", record)));

        Assert.Equal(TrackStatus.Unavailable, plan.Manifest.Tracks["a"].Status);
        Assert.Empty(plan.ToDownload);
    }

    // ---- duplicates, purity, playlist identity ---------------------------------------------

    [Fact]
    public void Duplicate_remote_entries_keep_first_position()
    {
        var plan = Plan(Remote(T("a"), T("b"), new RemoteTrack("a", "dup", null, null, 2)), null);

        Assert.Equal(["a", "b"], plan.ToDownload);
        Assert.Equal(0, plan.Manifest.Tracks["a"].RemoteIndex);
        Assert.Equal("Title a", plan.Manifest.Tracks["a"].Title);
    }

    [Fact]
    public void Input_manifest_is_not_modified()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "a.mp3")), ("gone", Rec(TrackStatus.Synced, "gone.mp3")),
            ("never", Rec(TrackStatus.Pending, file: null)));
        manifest.PlaylistTitle = "Old";

        var plan = Plan(Remote(T("a", title: "Changed")), manifest);

        Assert.NotSame(manifest, plan.Manifest);
        Assert.Equal("Old", manifest.PlaylistTitle);
        Assert.Equal(3, manifest.Tracks.Count);
        Assert.Equal(TrackStatus.Synced, manifest.Tracks["a"].Status);
        Assert.Equal("Title", manifest.Tracks["a"].Title);
        Assert.Equal(TrackStatus.Synced, manifest.Tracks["gone"].Status);
    }

    [Fact]
    public void Same_inputs_give_same_plan()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "a.mp3")), ("gone", Rec(TrackStatus.Synced, "gone.mp3")));
        var remote = Remote(T("a"), T("b"));
        var p1 = Plan(remote, manifest, Disk("a.mp3"));
        var p2 = Plan(remote, manifest, Disk("a.mp3"));

        Assert.Equal(p1.ToDownload, p2.ToDownload);
        Assert.Equal(p1.Changes, p2.Changes);
        Assert.Equal(p1.Untracked, p2.Untracked);
    }

    [Fact]
    public void Different_playlist_id_is_flagged()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "a.mp3")));
        manifest.PlaylistId = "PLother";
        var plan = Plan(Remote(T("a")), manifest, Disk("a.mp3"));

        Assert.True(plan.IsDifferentPlaylist);
        Assert.Equal("PL1", plan.Manifest.PlaylistId);
    }

    [Fact]
    public void Manifest_without_playlist_id_is_not_flagged()
    {
        var manifest = M();
        manifest.PlaylistId = null;
        var plan = Plan(Remote(T("a")), manifest);
        Assert.False(plan.IsDifferentPlaylist);
    }

    [Fact]
    public void Remote_without_title_keeps_known_title()
    {
        var manifest = M();
        manifest.PlaylistTitle = "Known";
        var remote = new RemotePlaylist(null, null, null, null, []);
        var plan = SyncPlanner.Plan(remote, manifest, DiskSnapshot.Empty, Url, true, Now);

        Assert.Equal("Known", plan.Manifest.PlaylistTitle);
    }

    // ---- untracked files and path handling -------------------------------------------------

    [Fact]
    public void Untracked_lists_only_unknown_audio_files_sorted()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "a.mp3")), ("gone", Rec(TrackStatus.RemovedRemote, "gone.opus")));
        var disk = Disk("a.mp3", "gone.opus", "z mine.flac", "B other.m4a", "cover.jpg", "notes.txt", "sub/deep.mp3");
        var plan = Plan(Remote(T("a")), manifest, disk);

        Assert.Equal(["B other.m4a", "sub/deep.mp3", "z mine.flac"], plan.Untracked);
    }

    [Fact]
    public void Manifest_paths_with_backslashes_match_disk()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, @"sub\a.mp3")));
        var plan = Plan(Remote(T("a")), manifest, Disk("sub/a.mp3"));

        Assert.Equal(TrackStatus.Synced, plan.Manifest.Tracks["a"].Status);
        Assert.Empty(plan.Untracked);
    }

    [Fact]
    public void Case_sensitivity_follows_the_snapshot_comparer()
    {
        var manifest = M(("a", Rec(TrackStatus.Synced, "Song.mp3")));

        var sensitive = SyncPlanner.Plan(Remote(T("a")), manifest,
            new DiskSnapshot(["song.mp3"], StringComparer.Ordinal), Url, true, Now);
        Assert.Equal(["a"], sensitive.ToDownload);
        Assert.Equal(["song.mp3"], sensitive.Untracked);

        var insensitive = SyncPlanner.Plan(Remote(T("a")), manifest,
            new DiskSnapshot(["song.mp3"], StringComparer.OrdinalIgnoreCase), Url, true, Now);
        Assert.Empty(insensitive.ToDownload);
        Assert.Empty(insensitive.Untracked);
    }

    // ---- a realistic mixed run -------------------------------------------------------------

    [Fact]
    public void Mixed_sync()
    {
        var manifest = M(
            ("keep", Rec(TrackStatus.Synced, "keep.mp3")),
            ("deleted-locally", Rec(TrackStatus.Synced, "dl.mp3")),
            ("failed", Rec(TrackStatus.Failed, null)),
            ("removed", Rec(TrackStatus.Synced, "removed.mp3")),
            ("readded", Rec(TrackStatus.RemovedRemote, "readded.mp3")),
            ("went-private", Rec(TrackStatus.Synced, "wp.mp3")));
        var remote = Remote(
            T("new1"), T("keep"), T("deleted-locally"), T("failed"), T("readded"),
            T("went-private", unavailable: "private video"), T("new2"));
        var disk = Disk("keep.mp3", "removed.mp3", "readded.mp3", "wp.mp3", "mine.mp3");

        var plan = Plan(remote, manifest, disk);

        Assert.Equal(["new1", "deleted-locally", "failed", "new2"], plan.ToDownload);
        var s = plan.Manifest.Tracks.ToDictionary(kv => kv.Key, kv => kv.Value.Status);
        Assert.Equal(TrackStatus.Synced, s["keep"]);
        Assert.Equal(TrackStatus.RemovedRemote, s["removed"]);
        Assert.Equal(TrackStatus.Synced, s["readded"]);
        Assert.Equal(TrackStatus.Unavailable, s["went-private"]);
        Assert.Equal(["mine.mp3"], plan.Untracked);
        Assert.Equal(8, plan.Manifest.Tracks.Count);
    }

    // ---- helpers ---------------------------------------------------------------------------

    [Fact]
    public void Stores_the_cover_and_keeps_it_when_the_remote_has_none()
    {
        const string cover = "https://i.ytimg.com/pl_c/PL1/studio_square_thumbnail.jpg?sqp=x";
        var remote = Remote(T("a")) with { Thumbnails = [new RemoteThumbnail(cover, 720, 720)] };
        var first = Plan(remote, null);
        Assert.Equal(cover, first.Manifest.CoverUrl);

        var second = Plan(Remote(T("a")), first.Manifest);
        Assert.Equal(cover, second.Manifest.CoverUrl);
    }

    // ---- same song under different ids -----------------------------------------------------

    private static RemoteTrack Song(string id, string title = "Song", string uploader = "Artist", double duration = 200) =>
        new(id, title, uploader, duration, -1);

    [Fact]
    public void Duplicate_song_is_downloaded_once()
    {
        var plan = Plan(Remote(Song("a"), T("x"), Song("b", "Artist - Song (Official Video)", "ArtistVEVO", 201)), null,
            skipDuplicates: true);

        Assert.Equal(["a", "x"], plan.ToDownload);
        var b = plan.Manifest.Tracks["b"];
        Assert.Equal(TrackStatus.Duplicate, b.Status);
        Assert.Equal("a", b.DuplicateOf);
        Assert.Contains(new TrackChange("b", "Artist - Song (Official Video)", null, TrackStatus.Duplicate), plan.Changes);
    }

    [Fact]
    public void Duplicates_are_downloaded_when_the_setting_is_off()
    {
        var plan = Plan(Remote(Song("a"), Song("b")), null, skipDuplicates: false);
        Assert.Equal(["a", "b"], plan.ToDownload);
    }

    [Fact]
    public void Already_downloaded_song_is_not_downloaded_again_under_a_new_id()
    {
        var old = Rec(TrackStatus.Synced, "Song.mp3", title: "Song", index: 1);
        old.Uploader = "Artist - Topic";
        old.DurationSec = 200;
        var plan = Plan(Remote(Song("new"), Song("old", uploader: "Artist - Topic")), M(("old", old)), Disk("Song.mp3"),
            skipDuplicates: true);

        Assert.Empty(plan.ToDownload);
        Assert.Equal(TrackStatus.Synced, plan.Manifest.Tracks["old"].Status);
        Assert.Equal("old", plan.Manifest.Tracks["new"].DuplicateOf);
    }

    [Fact]
    public void Song_removed_remotely_but_still_on_disk_counts_as_downloaded()
    {
        var old = Rec(TrackStatus.Synced, "Song.mp3", title: "Song");
        old.Uploader = "Artist";
        old.DurationSec = 200;
        var plan = Plan(Remote(Song("reupload")), M(("old", old)), Disk("Song.mp3"), skipDuplicates: true);

        Assert.Empty(plan.ToDownload);
        Assert.Equal(TrackStatus.RemovedRemote, plan.Manifest.Tracks["old"].Status);
        Assert.Equal(TrackStatus.Duplicate, plan.Manifest.Tracks["reupload"].Status);
    }

    [Fact]
    public void Duplicate_is_downloaded_once_the_original_leaves_the_playlist()
    {
        var dup = Rec(TrackStatus.Duplicate, null, title: "Song", index: 1);
        dup.Uploader = "Artist";
        dup.DurationSec = 200;
        dup.DuplicateOf = "a";
        var a = Rec(TrackStatus.Pending, null, title: "Song");
        a.Uploader = "Artist";
        a.DurationSec = 200;

        var plan = Plan(Remote(Song("b")), M(("a", a), ("b", dup)), skipDuplicates: true);

        Assert.Equal(["b"], plan.ToDownload);
        Assert.Equal(TrackStatus.Pending, plan.Manifest.Tracks["b"].Status);
        Assert.Null(plan.Manifest.Tracks["b"].DuplicateOf);
        Assert.False(plan.Manifest.Tracks.ContainsKey("a"));
    }

    [Fact]
    public void Unavailable_entry_is_not_an_original()
    {
        var plan = Plan(Remote(new RemoteTrack("gone", "Song", "Artist", 200, -1, "private video"), Song("b")), null,
            skipDuplicates: true);
        Assert.Equal(["b"], plan.ToDownload);
    }

    private static SyncPlan Plan(RemotePlaylist remote, Manifest? manifest, DiskSnapshot? disk = null,
        bool redownloadMissing = true, bool skipDuplicates = false) =>
        SyncPlanner.Plan(remote, manifest, disk ?? Disk(), Url, redownloadMissing, Now, skipDuplicates);

    /// <summary>Tracks built with <see cref="T"/> get their position from the argument order.</summary>
    private static RemotePlaylist Remote(params RemoteTrack[] tracks) =>
        new("PL1", "My Mix", "Owner", Url, tracks.Select((t, i) => t.Index < 0 ? t with { Index = i } : t).ToList());

    private static RemoteTrack T(string id, string? title = null, string? unavailable = null) =>
        new(id, unavailable is null ? title ?? "Title " + id : title, unavailable is null ? "Uploader " + id : null,
            unavailable is null ? 180 : null, -1, unavailable);

    private static RemotePlaylist Remote() => new("PL1", "My Mix", "Owner", Url, []);

    private static Manifest M(params (string Id, TrackRecord Record)[] tracks)
    {
        var m = new Manifest { PlaylistUrl = Url, PlaylistId = "PL1", PlaylistTitle = "My Mix" };
        foreach (var (id, r) in tracks) m.Tracks[id] = r;
        return m;
    }

    private static TrackRecord Rec(TrackStatus status, string? file, string title = "Title", int index = 0,
        string? error = null) => new()
    {
        Status = status,
        File = file,
        Title = title,
        Uploader = "Uploader",
        DurationSec = 100,
        RemoteIndex = index,
        FirstSeen = Then,
        LastSeenRemote = Then,
        DownloadedAt = file is null ? null : Then,
        LastError = error,
    };

    private static DiskSnapshot Disk(params string[] files) => new(files, StringComparer.Ordinal);
}
