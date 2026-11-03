namespace PlaylistSync.Core.Models;

/// <summary>
/// Per-directory sync state (<c>.playlist-sync.json</c>). Source of truth for track status.
/// Tracks are keyed by video id. Mutable on purpose: the sync service updates it in place
/// and persists it after every finished track.
/// </summary>
public sealed class Manifest
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string? PlaylistUrl { get; set; }
    public string? PlaylistTitle { get; set; }
    public Dictionary<string, TrackRecord> Tracks { get; set; } = new(StringComparer.Ordinal);
}

public sealed class TrackRecord
{
    public string? Title { get; set; }
    public string? Uploader { get; set; }
    public double? DurationSec { get; set; }
    /// <summary>File name relative to the playlist directory; null until downloaded.</summary>
    public string? File { get; set; }
    public TrackStatus Status { get; set; } = TrackStatus.Pending;
    /// <summary>0-based position in the remote playlist at the last sync.</summary>
    public int? RemoteIndex { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset? LastSeenRemote { get; set; }
    public DateTimeOffset? DownloadedAt { get; set; }
    public string? LastError { get; set; }
}
