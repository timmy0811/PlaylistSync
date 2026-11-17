using System.Text.Json;

namespace PlaylistSync.Core.Sources;

/// <summary>
/// Parses the output of <c>yt-dlp --flat-playlist --dump-single-json</c>.
/// Duplicate video ids keep their first position. Entries that are not videos are skipped.
/// </summary>
internal static class FlatPlaylistParser
{
    /// <summary>Titles YouTube uses for entries that are listed but can't be played.</summary>
    private static readonly Dictionary<string, string> UnavailableTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["[Private video]"] = "private video",
        ["[Deleted video]"] = "deleted video",
        ["[Unavailable video]"] = "unavailable video",
    };

    public static RemotePlaylist Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new PlaylistFetchException(FetchErrorKind.BadOutput, "yt-dlp returned output that is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new PlaylistFetchException(FetchErrorKind.BadOutput, "yt-dlp returned no playlist data.");

            var type = String(root, "_type");
            if (type != "playlist" || !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                throw new PlaylistFetchException(FetchErrorKind.NotAPlaylist, type is null or "video"
                    ? "This URL points to a single video, not a playlist."
                    : "This URL is not a playlist.");
            }

            var tracks = new List<RemoteTrack>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var nestedPlaylists = 0;

            foreach (var e in entries.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (String(e, "_type") == "playlist")
                {
                    nestedPlaylists++;
                    continue;
                }
                // Flat video entries carry ie_key "Youtube"; anything else (tabs, channels) isn't a track.
                var ieKey = String(e, "ie_key");
                if (ieKey is not null && ieKey != "Youtube") continue;

                var id = String(e, "id");
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;

                var title = String(e, "title");
                tracks.Add(new RemoteTrack(
                    id,
                    title,
                    String(e, "channel") ?? String(e, "uploader"),
                    Number(e, "duration"),
                    tracks.Count,
                    UnavailableReason(e, title)));
            }

            if (tracks.Count == 0 && nestedPlaylists > 0)
            {
                throw new PlaylistFetchException(FetchErrorKind.NotAPlaylist,
                    "This URL is a channel or a page with several playlists. Use the URL of a single playlist.");
            }

            var reported = Number(root, "playlist_count");
            return new RemotePlaylist(
                String(root, "id"),
                String(root, "title"),
                String(root, "channel") ?? String(root, "uploader"),
                String(root, "webpage_url"),
                tracks,
                reported is { } r ? (int)r : null)
            {
                Thumbnails = Thumbnails(root),
            };
        }
    }

    private static List<RemoteThumbnail> Thumbnails(JsonElement root)
    {
        var list = new List<RemoteThumbnail>();
        if (!root.TryGetProperty("thumbnails", out var thumbs) || thumbs.ValueKind != JsonValueKind.Array) return list;
        foreach (var t in thumbs.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object || String(t, "url") is not { Length: > 0 } url) continue;
            list.Add(new RemoteThumbnail(url, Number(t, "width") is { } w ? (int)w : null, Number(t, "height") is { } h ? (int)h : null));
        }
        return list;
    }

    private static string? UnavailableReason(JsonElement e, string? title)
    {
        if (title is not null && UnavailableTitles.TryGetValue(title.Trim(), out var reason)) return reason;
        if (String(e, "availability") == "private") return "private video";
        if (String(e, "live_status") == "is_upcoming") return "not released yet";
        // Without a signed-in session, YouTube Music lists deleted videos with nothing but their id (signed in, they
        // read "[Deleted video]"). Real entries always carry a title.
        if (title is null && Number(e, "duration") is null && String(e, "channel") is null && String(e, "uploader") is null)
            return "deleted video";
        return null;
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
