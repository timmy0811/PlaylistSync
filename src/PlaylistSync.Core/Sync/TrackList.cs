using PlaylistSync.Core.Models;

namespace PlaylistSync.Core.Sync;

/// <summary>Filter chips above the track list.</summary>
public enum TrackFilter
{
    All,
    /// <summary>Removed in the remote playlist; the file is still here.</summary>
    Removed,
    /// <summary>Failed downloads and files missing on disk.</summary>
    Problems,
    /// <summary>Private / deleted remotely.</summary>
    Unavailable,
    /// <summary>Audio files in the folder that the manifest doesn't know about.</summary>
    Untracked,
    /// <summary>Songs listed again under another video id, not downloaded.</summary>
    Duplicates,
}

/// <summary>One row of the track list: a manifest track (<see cref="Record"/> set) or an untracked file.</summary>
public sealed record TrackListItem(string? Id, TrackRecord? Record, string? UntrackedFile)
{
    public bool IsUntracked => Record is null;

    /// <summary>Unique within one list (video ids and file paths can't collide thanks to the prefix).</summary>
    public string Key => Id ?? "file:" + UntrackedFile;

    public static TrackListItem Track(string id, TrackRecord record) => new(id, record, null);
    public static TrackListItem Untracked(string file) => new(null, null, file);
}

/// <summary>Pure helpers that turn a manifest + untracked files into the rows shown in the UI.</summary>
public static class TrackList
{
    /// <summary>
    /// Rows in display order: tracks still in the remote playlist by remote position, then removed-in-remote tracks
    /// (by their last known position), then untracked files by path.
    /// </summary>
    public static IReadOnlyList<TrackListItem> Build(Manifest? manifest, IEnumerable<string> untracked)
    {
        var tracks = (manifest?.Tracks ?? Enumerable.Empty<KeyValuePair<string, TrackRecord>>())
            .OrderBy(kv => kv.Value.Status == TrackStatus.RemovedRemote ? 1 : 0)
            .ThenBy(kv => kv.Value.RemoteIndex ?? int.MaxValue)
            .ThenBy(kv => kv.Value.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => TrackListItem.Track(kv.Key, kv.Value));
        return tracks.Concat(untracked.Select(TrackListItem.Untracked)).ToList();
    }

    /// <param name="status">Null for an untracked file.</param>
    public static bool Matches(TrackFilter filter, TrackStatus? status) => filter switch
    {
        TrackFilter.All => true,
        TrackFilter.Removed => status == TrackStatus.RemovedRemote,
        TrackFilter.Problems => status is TrackStatus.Failed or TrackStatus.MissingLocal,
        TrackFilter.Unavailable => status == TrackStatus.Unavailable,
        TrackFilter.Untracked => status is null,
        TrackFilter.Duplicates => status == TrackStatus.Duplicate,
        _ => false,
    };

    /// <summary>
    /// Case-insensitive search over title, uploader, file name and video id. Every whitespace-separated term must
    /// match somewhere. An empty query matches everything.
    /// </summary>
    public static bool MatchesSearch(string? query, TrackListItem item)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var fields = new[] { item.Record?.Title, item.Record?.Uploader, item.Record?.File ?? item.UntrackedFile, item.Id };
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(term => fields.Any(f => f is not null && f.Contains(term, StringComparison.CurrentCultureIgnoreCase)));
    }

    /// <summary>
    /// The user may delete a track's file only when the track is gone remotely (removed or unavailable). Files of
    /// tracks still in the playlist would just be downloaded again, and untracked files are never touched.
    /// </summary>
    public static bool CanDeleteFile(TrackRecord record) =>
        record.File is not null && record.Status is TrackStatus.RemovedRemote or TrackStatus.Unavailable;

    /// <summary>
    /// The full path of a manifest file entry, or null if it points outside <paramref name="directory"/> or at our
    /// own bookkeeping (manifest, temp folder). Guards against hand-edited manifests.
    /// </summary>
    public static string? ResolveFile(string directory, string relativeFile)
    {
        if (string.IsNullOrWhiteSpace(relativeFile) || Path.IsPathRooted(relativeFile)) return null;
        string root, full;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            full = Path.GetFullPath(Path.Combine(root, relativeFile.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        var comparer = DiskSnapshot.PlatformComparer;
        if (full.Length <= prefix.Length || !comparer.Equals(full[..prefix.Length], prefix)) return null;

        var relative = full[prefix.Length..];
        var first = relative.Split(Path.DirectorySeparatorChar)[0];
        if (comparer.Equals(first, Storage.ManifestStore.FileName) || comparer.Equals(first, SyncService.TempDirName)) return null;
        return full;
    }
}
