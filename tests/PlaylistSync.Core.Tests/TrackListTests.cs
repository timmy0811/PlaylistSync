using PlaylistSync.Core.Models;
using PlaylistSync.Core.Sync;

namespace PlaylistSync.Core.Tests;

public sealed class TrackListTests
{
    private static TrackRecord R(TrackStatus status, int? index, string? title = null, string? file = null) =>
        new() { Status = status, RemoteIndex = index, Title = title, File = file };

    [Fact]
    public void Orders_by_remote_position_then_removed_then_untracked()
    {
        var m = new Manifest();
        m.Tracks["c"] = R(TrackStatus.Synced, 2);
        m.Tracks["gone2"] = R(TrackStatus.RemovedRemote, 1, file: "g2.mp3");
        m.Tracks["a"] = R(TrackStatus.Pending, 0);
        m.Tracks["gone1"] = R(TrackStatus.RemovedRemote, 0, file: "g1.mp3");
        m.Tracks["b"] = R(TrackStatus.Unavailable, 1);

        var rows = TrackList.Build(m, ["x.mp3"]);

        Assert.Equal(["a", "b", "c", "gone1", "gone2", "file:x.mp3"], rows.Select(r => r.Key));
        Assert.True(rows[^1].IsUntracked);
        Assert.Equal("x.mp3", rows[^1].UntrackedFile);
    }

    [Fact]
    public void No_manifest_lists_only_untracked_files()
    {
        var rows = TrackList.Build(null, ["a.mp3", "b.flac"]);
        Assert.All(rows, r => Assert.True(r.IsUntracked));
        Assert.Equal(2, rows.Count);
    }

    [Theory]
    [InlineData(TrackFilter.All, TrackStatus.Synced, true)]
    [InlineData(TrackFilter.Removed, TrackStatus.RemovedRemote, true)]
    [InlineData(TrackFilter.Removed, TrackStatus.Unavailable, false)]
    [InlineData(TrackFilter.Problems, TrackStatus.Failed, true)]
    [InlineData(TrackFilter.Problems, TrackStatus.MissingLocal, true)]
    [InlineData(TrackFilter.Problems, TrackStatus.Pending, false)]
    [InlineData(TrackFilter.Unavailable, TrackStatus.Unavailable, true)]
    [InlineData(TrackFilter.Unavailable, TrackStatus.RemovedRemote, false)]
    [InlineData(TrackFilter.Untracked, TrackStatus.Synced, false)]
    [InlineData(TrackFilter.Duplicates, TrackStatus.Duplicate, true)]
    [InlineData(TrackFilter.Duplicates, TrackStatus.Pending, false)]
    public void Filters_by_status(TrackFilter filter, TrackStatus status, bool expected) =>
        Assert.Equal(expected, TrackList.Matches(filter, status));

    [Fact]
    public void Untracked_files_match_all_and_untracked_only()
    {
        Assert.True(TrackList.Matches(TrackFilter.All, null));
        Assert.True(TrackList.Matches(TrackFilter.Untracked, null));
        Assert.False(TrackList.Matches(TrackFilter.Removed, null));
        Assert.False(TrackList.Matches(TrackFilter.Problems, null));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("daft", true)]
    [InlineData("DAFT punk", true)]
    [InlineData("punk around", true)]
    [InlineData("vid123", true)]
    [InlineData(".mp3", true)]
    [InlineData("daft nope", false)]
    public void Search_needs_every_term(string query, bool expected)
    {
        var item = TrackListItem.Track("vid123",
            new TrackRecord { Title = "Around the World", Uploader = "Daft Punk", File = "Daft Punk - Around the World.mp3" });
        Assert.Equal(expected, TrackList.MatchesSearch(query, item));
    }

    [Fact]
    public void Search_covers_untracked_file_names()
    {
        Assert.True(TrackList.MatchesSearch("bonus", TrackListItem.Untracked("Bonus Track.mp3")));
        Assert.False(TrackList.MatchesSearch("other", TrackListItem.Untracked("Bonus Track.mp3")));
    }

    [Theory]
    [InlineData(TrackStatus.RemovedRemote, "a.mp3", true)]
    [InlineData(TrackStatus.Unavailable, "a.mp3", true)]
    [InlineData(TrackStatus.Unavailable, null, false)]
    [InlineData(TrackStatus.Synced, "a.mp3", false)]
    [InlineData(TrackStatus.MissingLocal, "a.mp3", false)]
    [InlineData(TrackStatus.Failed, "a.mp3", false)]
    public void Only_files_of_tracks_gone_remotely_can_be_deleted(TrackStatus status, string? file, bool expected) =>
        Assert.Equal(expected, TrackList.CanDeleteFile(new TrackRecord { Status = status, File = file }));

    [Fact]
    public void Resolves_files_inside_the_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "music");
        Assert.Equal(Path.Combine(dir, "a.mp3"), TrackList.ResolveFile(dir, "a.mp3"));
        Assert.Equal(Path.Combine(dir, "sub", "a.mp3"), TrackList.ResolveFile(dir, "sub/a.mp3"));
        Assert.Equal(Path.Combine(dir, "a.mp3"), TrackList.ResolveFile(dir + Path.DirectorySeparatorChar, "a.mp3"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../a.mp3")]
    [InlineData("sub/../../a.mp3")]
    [InlineData(".")]
    [InlineData(".playlist-sync.json")]
    [InlineData(".playlist-sync-tmp/id/a.mp3")]
    public void Rejects_files_outside_the_folder_or_our_own(string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), "music");
        Assert.Null(TrackList.ResolveFile(dir, file));
    }

    [Fact]
    public void Rejects_absolute_paths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "music");
        Assert.Null(TrackList.ResolveFile(dir, Path.Combine(dir, "a.mp3")));
    }
}
