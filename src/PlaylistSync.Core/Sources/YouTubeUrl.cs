namespace PlaylistSync.Core.Sources;

/// <summary>Offline checks on YouTube / YouTube Music URLs.</summary>
public static class YouTubeUrl
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be",
    };

    public static bool IsYouTube(string? url) => TryParse(url, out _);

    /// <summary>The <c>list</c> query parameter, if any (playlist, watch-in-playlist, YouTube Music).</summary>
    public static string? PlaylistId(string? url)
    {
        if (!TryParse(url, out var uri)) return null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || !part.AsSpan(0, eq).Equals("list", StringComparison.Ordinal)) continue;
            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            return value.Length > 0 ? value : null;
        }
        return null;
    }

    public static string VideoUrl(string videoId) => "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(videoId);

    private static bool TryParse(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) return false;
        if (!Hosts.Contains(parsed.Host)) return false;
        uri = parsed;
        return true;
    }
}
