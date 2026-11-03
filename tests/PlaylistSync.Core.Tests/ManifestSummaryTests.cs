using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playlists;

namespace PlaylistSync.Core.Tests;

public sealed class ManifestSummaryTests
{
    [Fact]
    public void Null_manifest_is_empty()
    {
        var s = ManifestSummary.From(null);
        Assert.Equal(0, s.InPlaylist);
        Assert.False(s.NeedsAttention);
    }

    [Fact]
    public void Counts_every_status()
    {
        var m = new Manifest();
        var statuses = new[]
        {
            TrackStatus.Synced, TrackStatus.Synced, TrackStatus.Pending, TrackStatus.Downloading, TrackStatus.Failed,
            TrackStatus.RemovedRemote, TrackStatus.Unavailable, TrackStatus.MissingLocal,
        };
        for (var i = 0; i < statuses.Length; i++)
            m.Tracks["id" + i] = new TrackRecord { Status = statuses[i] };

        var s = ManifestSummary.From(m);
        Assert.Equal(new ManifestSummary(2, 2, 1, 1, 1, 1), s);
        Assert.Equal(7, s.InPlaylist);
        Assert.True(s.NeedsAttention);
        Assert.Equal("2 synced · 2 pending · 1 failed · 1 missing locally · 1 removed in remote · 1 unavailable", s.Describe());
    }

    [Fact]
    public void Clean_manifest_needs_no_attention()
    {
        var m = new Manifest();
        m.Tracks["a"] = new TrackRecord { Status = TrackStatus.Synced };
        m.Tracks["b"] = new TrackRecord { Status = TrackStatus.Pending };
        var s = ManifestSummary.From(m);
        Assert.False(s.NeedsAttention);
        Assert.Equal("1 synced · 1 pending", s.Describe());
    }
}

public sealed class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    [InlineData(30 * 3600, "yesterday")]
    [InlineData(5 * 86400, "5 days ago")]
    public void Formats_recent_times(int secondsAgo, string expected) =>
        Assert.Equal(expected, RelativeTime.Format(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void Null_is_never() => Assert.Equal("never", RelativeTime.Format(null, Now));
}
