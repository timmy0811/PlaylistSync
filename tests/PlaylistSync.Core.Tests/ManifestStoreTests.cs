using PlaylistSync.Core.Models;
using PlaylistSync.Core.Storage;

namespace PlaylistSync.Core.Tests;

public sealed class ManifestStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManifestStore _store = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_manifest_returns_null()
    {
        Assert.Null(await _store.LoadAsync(_dir.Path, Ct));
    }

    [Fact]
    public async Task Round_trip_preserves_tracks_and_statuses()
    {
        var now = DateTimeOffset.Parse("2026-11-03T12:00:00+01:00");
        var m = new Manifest { PlaylistUrl = "https://youtube.com/playlist?list=X", PlaylistTitle = "X" };
        m.Tracks["abcDEF12345"] = new TrackRecord
        {
            Title = "Song", Uploader = "Artist", DurationSec = 201, File = "Artist - Song.mp3",
            Status = TrackStatus.Synced, RemoteIndex = 0, FirstSeen = now, DownloadedAt = now,
        };
        m.Tracks["gone0000000"] = new TrackRecord { Title = "Gone", Status = TrackStatus.RemovedRemote, FirstSeen = now };
        m.Tracks["priv0000000"] = new TrackRecord { Title = "[Private video]", Status = TrackStatus.Unavailable, FirstSeen = now };

        await _store.SaveAsync(_dir.Path, m, Ct);
        var loaded = await _store.LoadAsync(_dir.Path, Ct);

        Assert.NotNull(loaded);
        Assert.Equal("X", loaded.PlaylistTitle);
        Assert.Equal(3, loaded.Tracks.Count);
        Assert.Equal(TrackStatus.Synced, loaded.Tracks["abcDEF12345"].Status);
        Assert.Equal("Artist - Song.mp3", loaded.Tracks["abcDEF12345"].File);
        Assert.Equal(now, loaded.Tracks["abcDEF12345"].DownloadedAt);
        Assert.Equal(TrackStatus.RemovedRemote, loaded.Tracks["gone0000000"].Status);
        Assert.Equal(TrackStatus.Unavailable, loaded.Tracks["priv0000000"].Status);
        Assert.False(File.Exists(ManifestStore.PathFor(_dir.Path) + ".tmp"));
    }

    [Fact]
    public async Task Track_ids_are_case_sensitive()
    {
        var m = new Manifest();
        m.Tracks["aaa"] = new TrackRecord { Title = "lower" };
        m.Tracks["AAA"] = new TrackRecord { Title = "upper" };
        await _store.SaveAsync(_dir.Path, m, Ct);

        var loaded = await _store.LoadAsync(_dir.Path, Ct);

        Assert.Equal("lower", loaded!.Tracks["aaa"].Title);
        Assert.False(loaded.Tracks.ContainsKey("Aaa"));
    }

    [Fact]
    public async Task Corrupt_manifest_throws_and_is_left_untouched()
    {
        var file = ManifestStore.PathFor(_dir.Path);
        await File.WriteAllTextAsync(file, "{ broken", Ct);

        await Assert.ThrowsAsync<StorageException>(() => _store.LoadAsync(_dir.Path, Ct));
        Assert.Equal("{ broken", await File.ReadAllTextAsync(file, Ct));
    }

    [Fact]
    public async Task Newer_format_version_is_rejected()
    {
        await File.WriteAllTextAsync(ManifestStore.PathFor(_dir.Path), """{ "version": 99, "tracks": {} }""", Ct);
        await Assert.ThrowsAsync<StorageException>(() => _store.LoadAsync(_dir.Path, Ct));
    }

    [Fact]
    public async Task Parallel_saves_do_not_collide()
    {
        var m = new Manifest();
        m.Tracks["id"] = new TrackRecord { Title = "t" };
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _store.SaveAsync(_dir.Path, m, Ct)));
        Assert.NotNull(await _store.LoadAsync(_dir.Path, Ct));
    }
}
