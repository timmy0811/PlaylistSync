using Microsoft.Extensions.Logging.Abstractions;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playlists;
using PlaylistSync.Core.Storage;

namespace PlaylistSync.Core.Tests;

public sealed class PlaylistLibraryTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;
    private readonly ConfigStore _store;

    public PlaylistLibraryTests()
    {
        _paths = new AppPaths(_dir.File("cfg"), _dir.File("data"));
        _store = new ConfigStore(_paths, NullLogger<ConfigStore>.Instance);
    }

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlaylistLibrary NewLibrary() => new(_store, NullLogger<PlaylistLibrary>.Instance);

    private static PlaylistEntry Entry(string name) =>
        new() { Name = name, Url = "https://www.youtube.com/playlist?list=PL" + name, Directory = "/music/" + name };

    [Fact]
    public async Task Changes_are_persisted_and_reloaded_in_order()
    {
        var lib = NewLibrary();
        await lib.LoadAsync(Ct);
        Assert.Empty(lib.Entries);

        var a = Entry("a");
        var b = Entry("b");
        var c = Entry("c");
        await lib.AddAsync(a, Ct);
        await lib.AddAsync(b, Ct);
        await lib.AddAsync(c, Ct);
        await lib.UpdateAsync(b with { Name = "B2" }, Ct);
        await lib.RemoveAsync(a.Id, Ct);
        await lib.MoveAsync(c.Id, 0, Ct);

        var reloaded = NewLibrary();
        await reloaded.LoadAsync(Ct);
        Assert.Equal(["c", "B2"], reloaded.Entries.Select(e => e.Name));
        Assert.Equal(b.Id, reloaded.Entries[1].Id);
    }

    [Fact]
    public async Task Update_with_function_keeps_id_and_other_fields()
    {
        var lib = NewLibrary();
        var a = Entry("a");
        await lib.AddAsync(a, Ct);
        var when = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        await lib.UpdateAsync(a.Id, e => e with { LastSync = when, Id = Guid.NewGuid() }, Ct);

        var e = Assert.Single(lib.Entries);
        Assert.Equal(a.Id, e.Id);
        Assert.Equal(when, e.LastSync);
        Assert.Equal("a", e.Name);
    }

    [Fact]
    public async Task Unknown_id_throws_and_changes_nothing()
    {
        var lib = NewLibrary();
        await lib.AddAsync(Entry("a"), Ct);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => lib.RemoveAsync(Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => lib.UpdateAsync(Entry("x"), Ct));
        Assert.Single(lib.Entries);
    }

    [Fact]
    public async Task Duplicate_add_is_rejected()
    {
        var lib = NewLibrary();
        var a = Entry("a");
        await lib.AddAsync(a, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lib.AddAsync(a with { Name = "again" }, Ct));
        Assert.Equal("a", Assert.Single(lib.Entries).Name);
    }

    [Fact]
    public async Task Failed_save_leaves_memory_unchanged()
    {
        var lib = NewLibrary();
        await lib.AddAsync(Entry("a"), Ct);

        // Make the config dir unwritable by replacing it with a file.
        Directory.Delete(_paths.ConfigDir, recursive: true);
        await File.WriteAllTextAsync(_paths.ConfigDir, "", Ct);

        await Assert.ThrowsAnyAsync<IOException>(() => lib.AddAsync(Entry("b"), Ct));
        Assert.Equal("a", Assert.Single(lib.Entries).Name);
    }

    [Fact]
    public async Task Duplicate_ids_in_file_are_dropped_on_load()
    {
        var a = Entry("a");
        await _store.SaveLibraryAsync(new Library { Playlists = [a, a with { Name = "dupe" }, Entry("b")] }, Ct);

        var lib = NewLibrary();
        await lib.LoadAsync(Ct);
        Assert.Equal(["a", "b"], lib.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task Remove_never_touches_the_folder()
    {
        var folder = _dir.File("music");
        Directory.CreateDirectory(folder);
        var song = Path.Combine(folder, "song.mp3");
        await File.WriteAllTextAsync(song, "x", Ct);
        await File.WriteAllTextAsync(ManifestStore.PathFor(folder), "{}", Ct);

        var lib = NewLibrary();
        var e = Entry("a") with { Directory = folder };
        await lib.AddAsync(e, Ct);
        await lib.RemoveAsync(e.Id, Ct);

        Assert.True(File.Exists(song));
        Assert.True(File.Exists(ManifestStore.PathFor(folder)));
    }
}
