using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Storage;

namespace PlaylistSync.Core.Playlists;

/// <summary>
/// The in-memory library (<c>library.json</c>), saved after every change. Changes are serialized and
/// only applied in memory once the save succeeded, so memory and disk never disagree.
/// Removing an entry only forgets it: the folder, its audio files and its manifest are left alone.
/// </summary>
public sealed class PlaylistLibrary(ConfigStore store, ILogger<PlaylistLibrary> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Library _library = new();

    public IReadOnlyList<PlaylistEntry> Entries => _library.Playlists;

    public PlaylistEntry? Find(Guid id) => _library.Playlists.Find(p => p.Id == id);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var loaded = await store.LoadLibraryAsync(ct).ConfigureAwait(false);
            // Drop duplicate ids from a hand-edited file instead of failing later on lookups.
            var unique = loaded.Playlists.DistinctBy(p => p.Id).ToList();
            if (unique.Count != loaded.Playlists.Count)
                log.LogWarning("library.json contains duplicate ids; {Count} entries ignored", loaded.Playlists.Count - unique.Count);
            _library = loaded with { Playlists = unique };
            log.LogInformation("Library loaded: {Count} playlists", unique.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task AddAsync(PlaylistEntry entry, CancellationToken ct = default) =>
        ChangeAsync(list =>
        {
            if (list.Exists(p => p.Id == entry.Id))
                throw new InvalidOperationException($"A playlist with id {entry.Id} already exists.");
            list.Add(entry);
        }, ct);

    /// <summary>Replaces the entry with the same id.</summary>
    public Task UpdateAsync(PlaylistEntry entry, CancellationToken ct = default) =>
        ChangeAsync(list => list[IndexOf(list, entry.Id)] = entry, ct);

    /// <summary>Applies <paramref name="update"/> to the current version of the entry (safe against concurrent edits).</summary>
    public Task UpdateAsync(Guid id, Func<PlaylistEntry, PlaylistEntry> update, CancellationToken ct = default) =>
        ChangeAsync(list =>
        {
            var i = IndexOf(list, id);
            list[i] = update(list[i]) with { Id = id };
        }, ct);

    /// <summary>Forgets the entry. Never touches the folder.</summary>
    public Task RemoveAsync(Guid id, CancellationToken ct = default) =>
        ChangeAsync(list => list.RemoveAt(IndexOf(list, id)), ct);

    /// <summary>Moves an entry to <paramref name="newIndex"/> (sidebar order).</summary>
    public Task MoveAsync(Guid id, int newIndex, CancellationToken ct = default) =>
        ChangeAsync(list =>
        {
            var i = IndexOf(list, id);
            var entry = list[i];
            list.RemoveAt(i);
            list.Insert(Math.Clamp(newIndex, 0, list.Count), entry);
        }, ct);

    /// <summary>
    /// Puts the entries in <paramref name="order"/> into that order, keeping the slots of all other entries (playlists
    /// of the other kind, hidden ones) where they are. See <see cref="LibraryOrder.Apply"/>.
    /// </summary>
    public Task ReorderAsync(IReadOnlyList<Guid> order, CancellationToken ct = default) =>
        ChangeAsync(list =>
        {
            var next = LibraryOrder.Apply(list, order);
            list.Clear();
            list.AddRange(next);
        }, ct);

    private async Task ChangeAsync(Action<List<PlaylistEntry>> change, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = new List<PlaylistEntry>(_library.Playlists);
            change(list);
            var next = _library with { Playlists = list };
            await store.SaveLibraryAsync(next, ct).ConfigureAwait(false);
            _library = next;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static int IndexOf(List<PlaylistEntry> list, Guid id)
    {
        var i = list.FindIndex(p => p.Id == id);
        return i >= 0 ? i : throw new KeyNotFoundException($"No playlist with id {id}.");
    }
}

/// <summary>Ordering of a subset of the library (the playlists the sidebar shows).</summary>
public static class LibraryOrder
{
    /// <summary>
    /// The entries whose ids are in <paramref name="order"/> take the positions those entries hold in
    /// <paramref name="entries"/>, in the given order; every other entry stays at its index. Unknown and repeated ids
    /// are ignored. Pure.
    /// </summary>
    public static List<PlaylistEntry> Apply(IReadOnlyList<PlaylistEntry> entries, IReadOnlyList<Guid> order)
    {
        var byId = entries.ToDictionary(e => e.Id);
        var wanted = order.Distinct().Where(byId.ContainsKey).ToList();
        var subset = wanted.ToHashSet();
        var slots = entries.Select((e, i) => (e, i)).Where(x => subset.Contains(x.e.Id)).Select(x => x.i).ToList();

        var result = entries.ToList();
        for (var k = 0; k < slots.Count; k++) result[slots[k]] = byId[wanted[k]];
        return result;
    }
}
