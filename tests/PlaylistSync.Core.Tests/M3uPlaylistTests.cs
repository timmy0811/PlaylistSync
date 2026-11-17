using PlaylistSync.Core.Playback;

namespace PlaylistSync.Core.Tests;

public class M3uPlaylistTests
{
    [Fact]
    public void Build_writes_header_extinf_and_paths()
    {
        var a = Path.Combine(Path.GetTempPath(), "a.opus");
        var b = Path.Combine(Path.GetTempPath(), "b.opus");
        var text = M3uPlaylist.Build([new QueueItem(a, "Song A", 125.4), new QueueItem(b)]);

        var tmp = Path.GetTempPath();
        var a2 = Path.Combine(tmp, ".", "a.opus");
        var b2 = Path.Combine(tmp, ".", "b.opus");
        Assert.Equal($"#EXTM3U\n#EXTINF:125,Song A\n{a2}\n#EXTINF:-1,b\n{b2}\n", text);
    }

    [Fact]
    public void Paths_get_a_dot_segment_so_players_do_not_resume_the_last_played_entry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Music");
        var file = Path.Combine(dir, "Artist - Song.mp3");

        var written = M3uPlaylist.UnrememberedPath(file);

        Assert.Equal(Path.Combine(dir, ".", "Artist - Song.mp3"), written);
        Assert.NotEqual(file, written);
        Assert.Equal(file, Path.GetFullPath(written)); // still the same file
        Assert.Equal("x.opus", M3uPlaylist.UnrememberedPath("x.opus")); // no folder: unchanged
    }

    [Fact]
    public void Build_strips_line_breaks_from_titles()
    {
        var text = M3uPlaylist.Build([new QueueItem("x.opus", "Line1\nLine2")]);
        Assert.Contains("#EXTINF:-1,Line1 Line2\n", text);
    }

    [Fact]
    public void Shuffle_keeps_every_item_once()
    {
        var items = Enumerable.Range(0, 50).ToList();
        var shuffled = M3uPlaylist.Shuffle(items, new Random(42));

        Assert.Equal(items, shuffled.Order());
        Assert.NotEqual(items, shuffled);
        Assert.Equal(Enumerable.Range(0, 50), items); // input untouched
    }
}
