namespace PlaylistSync.Core.Sources;

/// <summary>Chooses the playlist image to show.</summary>
public static class PlaylistCover
{
    /// <summary>
    /// True for a cover the owner uploaded (<c>i.ytimg.com/pl_c/&lt;playlist id&gt;/…</c>), as opposed to YouTube's
    /// fallback, the first video's 16:9 thumbnail (<c>i.ytimg.com/vi/&lt;video id&gt;/…</c>).
    /// </summary>
    public static bool IsCustom(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath.StartsWith("/pl_c/", StringComparison.Ordinal);

    /// <summary>
    /// True for YouTube's fallback image, a video thumbnail (<c>i.ytimg.com/vi/&lt;video id&gt;/…</c>). That is the first
    /// track's picture rather than the playlist's, so it isn't saved into the folder as the cover.
    /// </summary>
    public static bool IsVideoThumbnail(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.AbsolutePath.StartsWith("/vi/", StringComparison.Ordinal) ||
         uri.AbsolutePath.StartsWith("/vi_webp/", StringComparison.Ordinal));

    /// <summary>
    /// The URL of the best image: custom covers first, then square ones (wide ones for <paramref name="preferWide"/>,
    /// the cover of a video playlist), then the largest. Only https images from YouTube's image hosts qualify (see
    /// <see cref="IsAllowedHost"/>). Null if there is none.
    /// </summary>
    public static string? Pick(IEnumerable<RemoteThumbnail> thumbnails, bool preferWide = false) =>
        thumbnails
            .Where(t => IsAllowedHost(t.Url))
            .OrderByDescending(t => IsCustom(t.Url))
            .ThenByDescending(t => preferWide
                ? t.Width is { } w && t.Height is { } h && w > h
                : t.Width is { } sw && sw == t.Height)
            .ThenByDescending(t => (long)(t.Width ?? 0) * (t.Height ?? 0))
            .Select(t => t.Url)
            .FirstOrDefault();

    /// <summary>https on a YouTube / Google image host. Guards against fetching arbitrary URLs from an edited manifest.</summary>
    public static bool IsAllowedHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        return HostIs(host, "ytimg.com") || HostIs(host, "ggpht.com") || HostIs(host, "googleusercontent.com");
    }

    private static bool HostIs(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
