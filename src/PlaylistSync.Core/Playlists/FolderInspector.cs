using PlaylistSync.Core.Storage;
using PlaylistSync.Core.Sync;

namespace PlaylistSync.Core.Playlists;

public enum FolderKind
{
    /// <summary>Doesn't exist yet; the first sync creates it.</summary>
    Missing,
    Empty,
    /// <summary>Has files but no manifest: they show up as untracked and are left alone.</summary>
    HasFiles,
    /// <summary>Has a manifest of the same playlist (or one we can't compare): its history is reused.</summary>
    SamePlaylist,
    /// <summary>Has a manifest of another playlist id.</summary>
    DifferentPlaylist,
    /// <summary>Unusable: a file, unreadable, or a corrupt manifest.</summary>
    Error,
}

/// <param name="FileCount">Visible files (recursive) when there is no manifest.</param>
/// <param name="ManifestTracks">Tracks in an existing manifest.</param>
/// <param name="Message">User-facing description.</param>
public sealed record FolderInfo(FolderKind Kind, string Message, int FileCount = 0, int ManifestTracks = 0, string? ManifestTitle = null)
{
    public bool IsWarning => Kind == FolderKind.DifferentPlaylist;
    public bool IsError => Kind == FolderKind.Error;
}

/// <summary>Looks at a candidate playlist folder so the add/edit dialog can say what will happen.</summary>
public static class FolderInspector
{
    /// <param name="directory">Normalised full path.</param>
    /// <param name="playlistId">The playlist id the folder is meant for, if known.</param>
    public static async Task<FolderInfo> InspectAsync(string directory, string? playlistId, ManifestStore manifests, CancellationToken ct = default)
    {
        if (File.Exists(directory))
            return new FolderInfo(FolderKind.Error, "This path is a file, not a folder.");
        if (!Directory.Exists(directory))
            return new FolderInfo(FolderKind.Missing, "The folder will be created on the first sync.");

        Models.Manifest? manifest;
        try
        {
            manifest = await manifests.LoadAsync(directory, ct).ConfigureAwait(false);
        }
        catch (StorageException ex)
        {
            return new FolderInfo(FolderKind.Error, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FolderInfo(FolderKind.Error, $"The folder can't be read: {ex.Message}");
        }

        if (manifest is not null)
        {
            var count = manifest.Tracks.Count;
            var title = manifest.PlaylistTitle ?? manifest.PlaylistId;
            if (manifest.PlaylistId is { } oldId && playlistId is not null && !string.Equals(oldId, playlistId, StringComparison.Ordinal))
            {
                return new FolderInfo(FolderKind.DifferentPlaylist,
                    $"This folder was synced with another playlist ('{title}'). Syncing will mark its {count} tracks as removed in remote.",
                    ManifestTracks: count, ManifestTitle: title);
            }
            return new FolderInfo(FolderKind.SamePlaylist,
                $"Existing sync state found ({count} tracks). It will be reused, nothing is downloaded twice.",
                ManifestTracks: count, ManifestTitle: title);
        }

        int files;
        try
        {
            files = await Task.Run(() => DiskSnapshot.Scan(directory).Files.Count, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FolderInfo(FolderKind.Error, $"The folder can't be read: {ex.Message}");
        }
        return files == 0
            ? new FolderInfo(FolderKind.Empty, "The folder is empty.")
            : new FolderInfo(FolderKind.HasFiles,
                $"The folder already contains {files} file{(files == 1 ? "" : "s")}. They are kept and never touched; audio files show as untracked.",
                FileCount: files);
    }
}
