using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;

namespace PlaylistSync.Core.Sync;

public enum SyncStage
{
    Fetching,
    Planning,
    Downloading,
    Finishing,
}

/// <summary>
/// Progress of a running sync. Raised on background threads; the UI marshals them itself.
/// Records and manifests in events are snapshots, safe to keep.
/// </summary>
public abstract record SyncEvent;

public sealed record SyncStageChanged(SyncStage Stage) : SyncEvent;

/// <summary>The plan was made and saved; downloads are about to start.</summary>
public sealed record SyncPlanned(
    Manifest Manifest,
    IReadOnlyList<string> ToDownload,
    IReadOnlyList<TrackChange> Changes,
    IReadOnlyList<string> Untracked) : SyncEvent;

/// <summary>A track's record changed (download started, finished, failed, requeued on cancel).</summary>
public sealed record TrackUpdated(string Id, TrackRecord Record, int Completed, int Total) : SyncEvent;

/// <summary>Byte / step progress of a track being downloaded.</summary>
public sealed record TrackProgress(string Id, DownloadProgress Progress) : SyncEvent;

/// <param name="Manifest">Final state (a snapshot).</param>
/// <param name="Downloaded">Tracks downloaded in this run.</param>
/// <param name="Failed">Tracks that failed in this run (status <see cref="TrackStatus.Failed"/>).</param>
/// <param name="BecameUnavailable">Tracks whose download showed they are gone (status <see cref="TrackStatus.Unavailable"/>).</param>
public sealed record SyncResult(
    Manifest Manifest,
    IReadOnlyList<TrackChange> Changes,
    IReadOnlyList<string> Untracked,
    int Downloaded,
    int Failed,
    int BecameUnavailable,
    DateTimeOffset FinishedAt);

public enum SyncErrorKind
{
    /// <summary>The remote playlist could not be fetched; see the inner <see cref="Sources.PlaylistFetchException"/>.</summary>
    Fetch,
    /// <summary>The manifest could not be read or written.</summary>
    Manifest,
    /// <summary>The playlist folder can't be created or accessed.</summary>
    Directory,
    /// <summary>yt-dlp or ffmpeg is missing.</summary>
    ToolMissing,
    /// <summary>The folder's manifest belongs to another playlist. Retry with <c>allowDifferentPlaylist</c> after asking the user.</summary>
    DifferentPlaylist,
    /// <summary>A sync of the same folder is already running.</summary>
    AlreadyRunning,
}

/// <summary>A sync could not run. <see cref="Exception.Message"/> is user-facing.</summary>
public sealed class SyncException(SyncErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public SyncErrorKind Kind { get; } = kind;
}
