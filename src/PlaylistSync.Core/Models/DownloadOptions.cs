namespace PlaylistSync.Core.Models;

/// <summary>
/// Everything that affects how tracks are downloaded. Global defaults live in
/// <see cref="AppSettings.Download"/>; a playlist may carry a full override copy.
/// </summary>
public sealed record DownloadOptions
{
    // Audio
    public AudioFormat Format { get; init; } = AudioFormat.Mp3;
    public QualityMode QualityMode { get; init; } = QualityMode.Vbr;
    /// <summary>0 (best) … 10 (worst), used with <see cref="QualityMode.Vbr"/>.</summary>
    public int VbrLevel { get; init; }
    /// <summary>Bitrate in kbps, used with <see cref="QualityMode.Cbr"/>.</summary>
    public int CbrKbps { get; init; } = 320;

    // Metadata
    public bool EmbedMetadata { get; init; } = true;
    public bool EmbedThumbnail { get; init; } = true;
    public bool CropThumbnailSquare { get; init; } = true;
    public bool EmbedChapters { get; init; }
    public bool ParseArtistTitle { get; init; } = true;

    // Files
    public string FilenameTemplate { get; init; } = DefaultFilenameTemplate;
    public bool RestrictFilenames { get; init; }
    public bool TrackNumberPrefix { get; init; }
    public bool WriteM3u { get; init; }

    // Auth / network
    public CookieMode CookieMode { get; init; } = CookieMode.None;
    public string? CookiesFile { get; init; }
    public string? CookiesBrowser { get; init; }
    public string? CookiesBrowserProfile { get; init; }
    public string? Proxy { get; init; }
    /// <summary>yt-dlp --limit-rate value, e.g. "2M". Null = unlimited.</summary>
    public string? RateLimit { get; init; }
    public int SleepIntervalSeconds { get; init; }
    public int Retries { get; init; } = 10;

    // Extras
    public bool SponsorBlockRemoveOfftopic { get; init; }
    public bool RedownloadMissing { get; init; } = true;
    public string? ExtraArgs { get; init; }

    public const string DefaultFilenameTemplate = "%(artist,uploader)s - %(title)s.%(ext)s";
}
