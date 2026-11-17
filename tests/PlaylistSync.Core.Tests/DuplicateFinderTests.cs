using PlaylistSync.Core.Sync;

namespace PlaylistSync.Core.Tests;

public sealed class DuplicateFinderTests
{
    private static DuplicateCandidate C(string id, string title, string artist, double? duration = 200, bool hasFile = false) =>
        new(id, title, artist, duration, hasFile);

    [Fact]
    public void Topic_upload_and_music_video_are_the_same_song()
    {
        var d = DuplicateFinder.Find([
            C("topic", "Song Name", "Artist - Topic", 200),
            C("video", "Artist - Song Name (Official Video)", "ArtistVEVO", 202),
        ]);
        Assert.Equal(new Dictionary<string, string> { ["video"] = "topic" }, d);
    }

    [Fact]
    public void Earlier_track_is_kept()
    {
        var d = DuplicateFinder.Find([C("a", "Song", "Artist"), C("b", "Song (Audio)", "Artist - Topic"), C("c", "Song", "Artist")]);
        Assert.Equal("a", d["b"]);
        Assert.Equal("a", d["c"]);
        Assert.False(d.ContainsKey("a"));
    }

    [Fact]
    public void Downloaded_track_wins_over_earlier_one()
    {
        var d = DuplicateFinder.Find([C("new", "Song", "Artist"), C("old", "Song", "Artist", hasFile: true)]);
        Assert.Equal(new Dictionary<string, string> { ["new"] = "old" }, d);
    }

    [Fact]
    public void Two_downloaded_tracks_are_never_marked()
    {
        var d = DuplicateFinder.Find([C("a", "Song", "Artist", hasFile: true), C("b", "Song", "Artist", hasFile: true)]);
        Assert.Empty(d);
    }

    [Theory]
    [InlineData("Song", "Artist", 210)]          // different length (e.g. extended intro)
    [InlineData("Song (Live)", "Artist", 200)]   // other version
    [InlineData("Song (Remix)", "Artist", 200)]
    [InlineData("Other Song", "Artist", 200)]
    [InlineData("Song", "Someone Else", 200)]
    public void Different_songs_are_not_duplicates(string title, string artist, double duration)
    {
        Assert.Empty(DuplicateFinder.Find([C("a", "Song", "Artist", 200), C("b", title, artist, duration)]));
    }

    [Fact]
    public void Unknown_length_never_matches()
    {
        Assert.Empty(DuplicateFinder.Find([C("a", "Song", "Artist", null), C("b", "Song", "Artist", null)]));
        Assert.Empty(DuplicateFinder.Find([C("a", "Song", "Artist", 200), C("b", "Song", "Artist", null)]));
    }

    [Fact]
    public void Length_within_tolerance_matches()
    {
        var d = DuplicateFinder.Find([C("a", "Song", "Artist", 200), C("b", "Song", "Artist", 200 + DuplicateFinder.DurationToleranceSec)]);
        Assert.Equal("a", d["b"]);
    }
}
