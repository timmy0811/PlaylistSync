using PlaylistSync.Core.Models;
using PlaylistSync.Core.Storage;
using PlaylistSync.Core.Sync;

namespace PlaylistSync.Core.Tests;

public class DiskSnapshotTests
{
    [Fact]
    public void Scan_lists_files_recursively_with_forward_slashes_and_skips_hidden()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.mp3"), "");
        Directory.CreateDirectory(dir.File("sub"));
        File.WriteAllText(Path.Combine(dir.Path, "sub", "b.opus"), "");
        File.WriteAllText(dir.File(ManifestStore.FileName), "{}");
        File.WriteAllText(dir.File(".hidden.mp3"), "");

        var snap = DiskSnapshot.Scan(dir.Path);

        Assert.Equal(["a.mp3", "sub/b.opus"], snap.Files.Order(StringComparer.Ordinal));
        Assert.True(snap.Contains("a.mp3"));
        Assert.True(snap.Contains(@"sub\b.opus"));
        Assert.False(snap.Contains(ManifestStore.FileName));
    }

    [Fact]
    public void Scan_of_missing_directory_is_empty()
    {
        using var dir = new TempDir();
        Assert.Empty(DiskSnapshot.Scan(dir.File("does-not-exist")).Files);
    }

    [Fact]
    public void Manifest_clone_is_deep()
    {
        var m = new Manifest { PlaylistId = "PL1" };
        m.Tracks["a"] = new TrackRecord { Title = "A", Status = TrackStatus.Synced };

        var c = m.Clone();
        c.Tracks["a"].Status = TrackStatus.RemovedRemote;
        c.Tracks["b"] = new TrackRecord();

        Assert.Equal("PL1", c.PlaylistId);
        Assert.Equal(TrackStatus.Synced, m.Tracks["a"].Status);
        Assert.Single(m.Tracks);
        Assert.Equal(StringComparer.Ordinal, c.Tracks.Comparer);
    }
}
