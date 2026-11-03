using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playlists;
using PlaylistSync.Core.Storage;

namespace PlaylistSync.Core.Tests;

public sealed class FolderInspectorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManifestStore _manifests = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<FolderInfo> Inspect(string dir, string? id = "PL1") => FolderInspector.InspectAsync(dir, id, _manifests, Ct);

    [Fact]
    public async Task Missing_folder()
    {
        Assert.Equal(FolderKind.Missing, (await Inspect(_dir.File("nope"))).Kind);
    }

    [Fact]
    public async Task File_instead_of_folder_is_an_error()
    {
        var f = _dir.File("file");
        await File.WriteAllTextAsync(f, "x", Ct);
        Assert.True((await Inspect(f)).IsError);
    }

    [Fact]
    public async Task Empty_and_non_empty_folders()
    {
        Assert.Equal(FolderKind.Empty, (await Inspect(_dir.Path)).Kind);

        await File.WriteAllTextAsync(_dir.File("a.mp3"), "x", Ct);
        await File.WriteAllTextAsync(_dir.File(".hidden"), "x", Ct);
        var info = await Inspect(_dir.Path);
        Assert.Equal(FolderKind.HasFiles, info.Kind);
        Assert.Equal(1, info.FileCount);
    }

    [Fact]
    public async Task Same_and_different_playlist_manifests()
    {
        var m = new Manifest { PlaylistId = "PL1", PlaylistTitle = "Chill" };
        m.Tracks["a"] = new TrackRecord { Status = TrackStatus.Synced };
        await _manifests.SaveAsync(_dir.Path, m, Ct);

        var same = await Inspect(_dir.Path);
        Assert.Equal(FolderKind.SamePlaylist, same.Kind);
        Assert.Equal(1, same.ManifestTracks);

        var unknownId = await Inspect(_dir.Path, id: null);
        Assert.Equal(FolderKind.SamePlaylist, unknownId.Kind);

        var other = await Inspect(_dir.Path, id: "PL2");
        Assert.Equal(FolderKind.DifferentPlaylist, other.Kind);
        Assert.True(other.IsWarning);
        Assert.Contains("Chill", other.Message);
    }

    [Fact]
    public async Task Corrupt_manifest_is_an_error()
    {
        await File.WriteAllTextAsync(ManifestStore.PathFor(_dir.Path), "{ nope", Ct);
        Assert.True((await Inspect(_dir.Path)).IsError);
    }
}
