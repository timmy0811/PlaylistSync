namespace PlaylistSync.Core.Models;

public enum AudioFormat { Mp3, M4a, Opus, Flac, Ogg, Wav, Best }

public enum QualityMode { Vbr, Cbr }

public enum CookieMode { None, File, Browser }

public enum ThemeMode { System, Light, Dark }

/// <summary>What a playlist is downloaded as. Fixed when the playlist is added.</summary>
public enum PlaylistKind
{
    /// <summary>Audio files (the default).</summary>
    Music,
    /// <summary>Video files.</summary>
    Video,
}

/// <summary>Container of downloaded videos.</summary>
public enum VideoContainer
{
    /// <summary>MP4, falling back to MKV when the streams don't fit into MP4. Plays everywhere.</summary>
    Mp4,
    /// <summary>MKV: holds any stream combination without re-encoding.</summary>
    Mkv,
}

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
    /// <summary>
    /// The same song (artist, title, length) under another video id is already in the playlist, so this one is not
    /// downloaded (see <see cref="DownloadOptions.SkipDuplicates"/> and <see cref="TrackRecord.DuplicateOf"/>).
    /// </summary>
    Duplicate,
}
