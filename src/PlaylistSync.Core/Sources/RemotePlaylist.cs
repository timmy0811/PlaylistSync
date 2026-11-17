using PlaylistSync.Core.Models;

namespace PlaylistSync.Core.Sources;

/// <summary>One entry of a remote playlist, in remote order.</summary>
/// <param name="Id">YouTube video id: the track identity.</param>
/// <param name="Index">0-based position in the playlist (after removing duplicates).</param>
/// <param name="UnavailableReason">Non-null when the video is listed but can't be downloaded (private, deleted, upcoming).</param>
public sealed record RemoteTrack(
    string Id,
    string? Title,
    string? Uploader,
    double? DurationSec,
    int Index,
    string? UnavailableReason = null)
{
    public bool IsAvailable => UnavailableReason is null;
}

/// <summary>A playlist image offered by the site. Sizes are null when unknown.</summary>
public sealed record RemoteThumbnail(string Url, int? Width, int? Height);

/// <param name="Id">Remote playlist id, e.g. <c>PL…</c>.</param>
/// <param name="ReportedCount">Entry count the site reported, if any (may differ from <see cref="Tracks"/>).</param>
public sealed record RemotePlaylist(
    string? Id,
    string? Title,
    string? Uploader,
    string? WebpageUrl,
    IReadOnlyList<RemoteTrack> Tracks,
    int? ReportedCount = null)
{
    /// <summary>Playlist images (the user's custom cover, or YouTube's fallback from the first video).</summary>
    public IReadOnlyList<RemoteThumbnail> Thumbnails { get; init; } = [];
}

/// <summary>What a source needs from the settings to fetch a playlist.</summary>
public sealed record SourceContext(ToolSettings Tools, DownloadOptions Options);

public interface IPlaylistSource
{
    /// <summary>True if this source understands the URL (cheap, offline check).</summary>
    bool CanHandle(string url);

    /// <summary>Fetches the playlist's entries without downloading anything.</summary>
    /// <exception cref="PlaylistFetchException">The playlist could not be fetched; the message is user-facing.</exception>
    Task<RemotePlaylist> FetchAsync(string url, SourceContext context, CancellationToken ct = default);
}

public enum FetchErrorKind
{
    Other,
    /// <summary>yt-dlp is missing or unusable.</summary>
    ToolMissing,
    /// <summary>The URL is not a playlist (single video, channel, unsupported site).</summary>
    NotAPlaylist,
    /// <summary>YouTube wants a signed-in session (bot check, private playlist, age restriction).</summary>
    NeedsCookies,
    /// <summary>yt-dlp printed something we couldn't understand.</summary>
    BadOutput,
}

/// <summary>Fetching failed. <see cref="Exception.Message"/> is meant for the user.</summary>
public sealed class PlaylistFetchException(FetchErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public FetchErrorKind Kind { get; } = kind;
}
