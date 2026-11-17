using PlaylistSync.Core.Models;
using PlaylistSync.Core.Sources;

namespace PlaylistSync.Core.Sync;

/// <summary>A status change decided by the planner.</summary>
/// <param name="From">Null for a track seen for the first time.</param>
/// <param name="To">Null when the record is dropped (removed remotely, never downloaded).</param>
public sealed record TrackChange(string Id, string? Title, TrackStatus? From, TrackStatus? To);

/// <param name="Manifest">The updated manifest (a new object; the input is not modified).</param>
/// <param name="ToDownload">Video ids to download, in remote order.</param>
/// <param name="Changes">Status changes, for logging and the UI.</param>
/// <param name="Untracked">Audio files in the directory the manifest doesn't know about. Never touched.</param>
/// <param name="IsDifferentPlaylist">The directory's manifest belongs to another playlist id.</param>
public sealed record SyncPlan(
    Manifest Manifest,
    IReadOnlyList<string> ToDownload,
    IReadOnlyList<TrackChange> Changes,
    IReadOnlyList<string> Untracked,
    bool IsDifferentPlaylist);

/// <summary>
/// Pure diff: remote playlist + manifest + disk state → new manifest and download list.
/// No IO, no clock (the time is passed in), no mutation of its inputs.
/// </summary>
public static class SyncPlanner
{
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".opus", ".flac", ".ogg", ".oga", ".wav", ".webm", ".aac", ".mka", ".wma", ".alac", ".aiff",
    };

    /// <param name="playlistUrl">The library entry's URL, stored in the manifest.</param>
    /// <param name="redownloadMissing">Queue tracks whose file was deleted locally (otherwise they become <see cref="TrackStatus.MissingLocal"/>).</param>
    /// <param name="skipDuplicates">
    /// Don't download a track when the same song (artist, title, length) is already downloaded or comes earlier in the
    /// playlist under another video id; it becomes <see cref="TrackStatus.Duplicate"/> instead.
    /// </param>
    public static SyncPlan Plan(
        RemotePlaylist remote,
        Manifest? current,
        DiskSnapshot disk,
        string playlistUrl,
        bool redownloadMissing,
        DateTimeOffset now,
        bool skipDuplicates = false)
    {
        var manifest = current?.Clone() ?? new Manifest();
        var isDifferentPlaylist = current?.PlaylistId is { } oldId && remote.Id is { } newId &&
                                  !string.Equals(oldId, newId, StringComparison.Ordinal);

        manifest.PlaylistUrl = playlistUrl;
        manifest.PlaylistId = remote.Id ?? manifest.PlaylistId;
        manifest.PlaylistTitle = remote.Title ?? manifest.PlaylistTitle;
        manifest.CoverUrl = PlaylistCover.Pick(remote.Thumbnails) ?? manifest.CoverUrl;

        var toDownload = new List<string>();
        var changes = new List<TrackChange>();
        var inRemote = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = skipDuplicates
            ? DuplicateFinder.Find(DuplicateCandidates(remote, manifest, disk))
            : new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var track in remote.Tracks.OrderBy(t => t.Index))
        {
            if (!inRemote.Add(track.Id)) continue;

            var isNew = !manifest.Tracks.TryGetValue(track.Id, out var record);
            if (record is null)
            {
                record = new TrackRecord { FirstSeen = now };
                manifest.Tracks[track.Id] = record;
            }
            var before = isNew ? (TrackStatus?)null : record.Status;

            UpdateMetadata(record, track, overwrite: isNew || track.IsAvailable);
            record.RemoteIndex = track.Index;
            record.LastSeenRemote = now;
            record.DuplicateOf = null;

            var fileExists = record.File is not null && disk.Contains(record.File);
            if (!track.IsAvailable && record.ReplacementId is not null && fileExists)
            {
                // Gone, but its file came from a replacement upload: nothing is missing.
                record.Status = TrackStatus.Synced;
            }
            else if (!track.IsAvailable)
            {
                // Listed but private/deleted: never "removed", and the local file (if any) stays.
                record.Status = TrackStatus.Unavailable;
            }
            else if (fileExists)
            {
                // Covers re-added (RemovedRemote), available again (Unavailable) and a crash after the file was moved into place.
                record.Status = TrackStatus.Synced;
                record.LastError = null;
            }
            else if (duplicates.TryGetValue(track.Id, out var original))
            {
                // The same song under another id is (or will be) downloaded: one file is enough.
                record.Status = TrackStatus.Duplicate;
                record.DuplicateOf = original;
                record.LastError = null;
            }
            else if (record.File is not null && !redownloadMissing)
            {
                record.Status = TrackStatus.MissingLocal;
            }
            else
            {
                // New, Pending, Failed (retry), a stale Downloading from a crash, or a deleted file to re-download.
                record.Status = TrackStatus.Pending;
                toDownload.Add(track.Id);
            }

            if (before != record.Status) changes.Add(new TrackChange(track.Id, record.Title, before, record.Status));
        }

        foreach (var (id, record) in manifest.Tracks.ToList())
        {
            if (inRemote.Contains(id)) continue;

            if (record.File is null)
            {
                // Nothing on disk to highlight or delete: forget it.
                manifest.Tracks.Remove(id);
                changes.Add(new TrackChange(id, record.Title, record.Status, null));
            }
            else if (record.Status != TrackStatus.RemovedRemote)
            {
                changes.Add(new TrackChange(id, record.Title, record.Status, TrackStatus.RemovedRemote));
                record.Status = TrackStatus.RemovedRemote;
            }
        }

        return new SyncPlan(manifest, toDownload, changes, FindUntracked(manifest, disk), isDifferentPlaylist);
    }

    /// <summary>
    /// Everything a new download could duplicate: downloaded tracks (also those removed remotely) and the available
    /// remote tracks, in remote order. Unavailable tracks without a file can't be downloaded either way.
    /// </summary>
    private static List<DuplicateCandidate> DuplicateCandidates(RemotePlaylist remote, Manifest manifest, DiskSnapshot disk)
    {
        var list = new List<DuplicateCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in remote.Tracks.OrderBy(t => t.Index))
        {
            if (!seen.Add(track.Id)) continue;
            manifest.Tracks.TryGetValue(track.Id, out var record);
            var hasFile = record?.File is { } f && disk.Contains(f);
            if (!track.IsAvailable && !hasFile) continue;
            list.Add(track.IsAvailable
                ? new DuplicateCandidate(track.Id, track.Title ?? record?.Title, track.Uploader ?? record?.Uploader,
                    track.DurationSec ?? record?.DurationSec, hasFile)
                : new DuplicateCandidate(track.Id, record!.Title, record.Uploader, record.DurationSec, true));
        }
        foreach (var (id, record) in manifest.Tracks)
        {
            if (seen.Contains(id) || record.File is not { } file || !disk.Contains(file)) continue;
            list.Add(new DuplicateCandidate(id, record.Title, record.Uploader, record.DurationSec, true));
        }
        return list;
    }

    private static void UpdateMetadata(TrackRecord record, RemoteTrack track, bool overwrite)
    {
        // An unavailable entry only has placeholder data ("[Private video]"); keep what we knew.
        if (!overwrite) return;
        if (track.Title is not null) record.Title = track.Title;
        if (track.Uploader is not null) record.Uploader = track.Uploader;
        if (track.DurationSec is not null) record.DurationSec = track.DurationSec;
    }

    /// <summary>Audio files on disk that no manifest record points to, sorted by path.</summary>
    public static List<string> FindUntracked(Manifest? manifest, DiskSnapshot disk)
    {
        var known = new HashSet<string>(
            (manifest?.Tracks.Values ?? Enumerable.Empty<TrackRecord>()).Where(r => r.File is not null).Select(r => DiskSnapshot.Normalize(r.File!)),
            disk.Comparer);
        return disk.Files
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)) && !known.Contains(f))
            .Order(disk.Comparer)
            .ToList();
    }
}
