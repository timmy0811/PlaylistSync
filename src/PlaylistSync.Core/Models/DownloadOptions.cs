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

    // Cover
    /// <summary>Save the playlist's cover image into the folder as <c>cover.jpg</c>/<c>cover.png</c> (never overwrites one).</summary>
    public bool SaveFolderCover { get; init; }

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
    /// <summary>Download a song listed several times under different video ids (same artist, title and length) only once.</summary>
    public bool SkipDuplicates { get; init; } = true;
    /// <summary>
    /// When a track turns out to be gone (deleted, or a release the label pulled), search YouTube Music for the same song
    /// and download a matching upload instead. YouTube Music does this swap silently when playing the playlist.
    /// </summary>
    public bool FindReplacements { get; init; } = true;
    public string? ExtraArgs { get; init; }

    public const string DefaultFilenameTemplate = "%(artist,uploader)s - %(title)s.%(ext)s";

    /// <summary>
    /// The options a playlist syncs with: its override if it has one, else the global options. Cookies, proxy and the
    /// other network settings always come from <paramref name="global"/>, so e.g. setting up cookies once covers every
    /// playlist, including those with custom settings.
    /// </summary>
    public static DownloadOptions Effective(DownloadOptions global, DownloadOptions? playlistOverride) =>
        playlistOverride is null ? global : playlistOverride.WithNetworkFrom(global);

    /// <summary>A copy with the auth / network group taken from <paramref name="source"/>.</summary>
    public DownloadOptions WithNetworkFrom(DownloadOptions source) => this with
    {
        CookieMode = source.CookieMode,
        CookiesFile = source.CookiesFile,
        CookiesBrowser = source.CookiesBrowser,
        CookiesBrowserProfile = source.CookiesBrowserProfile,
        Proxy = source.Proxy,
        RateLimit = source.RateLimit,
        SleepIntervalSeconds = source.SleepIntervalSeconds,
        Retries = source.Retries,
    };
}
