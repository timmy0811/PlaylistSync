namespace PlaylistSync.Core.Models;

public enum AudioFormat { Mp3, M4a, Opus, Flac, Ogg, Wav, Best }

public enum QualityMode { Vbr, Cbr }

public enum CookieMode { None, File, Browser }

public enum ThemeMode { System, Light, Dark }

public enum TrackStatus
{
    /// <summary>In the remote playlist, not downloaded yet.</summary>
    Pending,
    Downloading,
    /// <summary>Downloaded and still in the remote playlist.</summary>
    Synced,
    /// <summary>No longer in the remote playlist. The local file is kept.</summary>
    RemovedRemote,
    /// <summary>Still listed remotely but private/deleted, so it can't be downloaded. Never treated as removed.</summary>
    Unavailable,
    /// <summary>The last download attempt failed; retried on the next sync.</summary>
    Failed,
    /// <summary>Recorded as downloaded, but the file is gone from disk.</summary>
    MissingLocal,
}
