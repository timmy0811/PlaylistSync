using PlaylistSync.Core.Download;

namespace PlaylistSync.Core.Sync;

/// <param name="Artist">Uploader / channel name as YouTube lists it (e.g. "Artist - Topic", "ArtistVEVO").</param>
/// <param name="HasFile">Already downloaded: kept as is and never reported as a duplicate.</param>
public sealed record DuplicateCandidate(string Id, string? Title, string? Artist, double? DurationSec, bool HasFile);

/// <summary>
/// Finds the same song listed under different video ids (e.g. the "Artist - Topic" upload and the official music
/// video): same normalized title, matching artist and the same length (± <see cref="DurationToleranceSec"/>).
/// Pure; tracks are hashed by title, so a playlist is checked in about linear time.
/// </summary>
public static class DuplicateFinder
{
    public const double DurationToleranceSec = ReplacementMatcher.DurationToleranceSec;

    private sealed record Kept(string Id, string Artist, double DurationSec);

    /// <summary>
    /// Maps each duplicate's id to the id of the track it repeats. Downloaded tracks win, then earlier ones in the
    /// given order. Tracks without a title, artist or length are never matched.
    /// </summary>
    public static Dictionary<string, string> Find(IReadOnlyList<DuplicateCandidate> candidates)
    {
        var duplicates = new Dictionary<string, string>(StringComparer.Ordinal);
        var kept = new Dictionary<string, List<Kept>>(StringComparer.Ordinal);

        foreach (var c in candidates.Where(c => c.HasFile).Concat(candidates.Where(c => !c.HasFile)))
        {
            if (c.DurationSec is not { } duration || string.IsNullOrWhiteSpace(c.Title)) continue;
            var keys = Keys(c.Title, c.Artist);
            if (keys.Count == 0) continue;

            if (!c.HasFile && FindOriginal(kept, keys, duration) is { } original)
            {
                duplicates[c.Id] = original;
                continue;
            }
            foreach (var (title, artist) in keys)
            {
                if (!kept.TryGetValue(title, out var list)) kept[title] = list = [];
                list.Add(new Kept(c.Id, artist, duration));
            }
        }
        return duplicates;
    }

    private static string? FindOriginal(Dictionary<string, List<Kept>> kept, List<(string Title, string Artist)> keys, double duration)
    {
        foreach (var (title, artist) in keys)
        {
            if (!kept.TryGetValue(title, out var list)) continue;
            foreach (var k in list)
            {
                if (Math.Abs(k.DurationSec - duration) <= DurationToleranceSec && ReplacementMatcher.ArtistMatches(artist, [k.Artist]))
                    return k.Id;
            }
        }
        return null;
    }

    /// <summary>
    /// (normalized title, artist) readings of a track: the title as is with the uploader as artist, and, for
    /// "Artist - Song" titles, the part after the dash with the part before it as artist.
    /// </summary>
    internal static List<(string Title, string Artist)> Keys(string title, string? uploader)
    {
        var keys = new List<(string, string)>(2);
        var artist = uploader is null ? null : CleanArtist(uploader);
        if (!string.IsNullOrEmpty(artist) && ReplacementMatcher.NormalizeTitle(title, artist) is { Length: > 0 } t)
            keys.Add((t, artist));

        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            var left = CleanArtist(title[..dash]);
            var right = ReplacementMatcher.NormalizeTitle(title[(dash + 3)..], null);
            if (left.Length > 0 && right.Length > 0 && !keys.Contains((right, left))) keys.Add((right, left));
        }
        return keys;
    }

    /// <summary>"Artist - Topic" / "ArtistVEVO" → "Artist".</summary>
    private static string CleanArtist(string artist)
    {
        var a = ReplacementMatcher.DisplayArtist(artist);
        if (a.Length > 4 && a.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase)) a = a[..^4].TrimEnd();
        return a;
    }
}
