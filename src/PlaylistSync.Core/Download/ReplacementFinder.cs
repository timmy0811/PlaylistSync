using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Download;

/// <summary>What is known about a gone track, from the manifest.</summary>
/// <param name="Artist">The uploader as listed in the playlist (e.g. "Mr. Bill" or "THz - Topic").</param>
public sealed record ReplacementQuery(string OriginalId, string Title, string Artist, double DurationSec);

/// <summary>An upload that matches the gone track.</summary>
public sealed record Replacement(string VideoId, string Title, string Artists, double DurationSec);

public enum ReplacementOutcome
{
    Found,
    /// <summary>The search ran and nothing matched closely enough.</summary>
    NotFound,
    /// <summary>The search itself failed (network, bot check, rate limit); worth trying again soon.</summary>
    Error,
}

public sealed record ReplacementResult(ReplacementOutcome Outcome, Replacement? Replacement = null, string? Error = null)
{
    public static readonly ReplacementResult NotFound = new(ReplacementOutcome.NotFound);
}

public interface IReplacementFinder
{
    /// <summary>
    /// Looks for an upload of the same song: same artist, same title (ignoring "feat." credits and "Official Audio"
    /// style suffixes) and the same length (± <see cref="ReplacementMatcher.DurationToleranceSec"/>). Never throws for
    /// search failures; only cancellation throws.
    /// </summary>
    Task<ReplacementResult> FindAsync(ReplacementQuery query, DownloadOptions options, DownloadTools tools, CancellationToken ct);
}

/// <summary>
/// Searches YouTube Music's songs with yt-dlp, then checks the hits whose title matches by reading their full metadata
/// (a flat search result carries no artist or length). Conservative on purpose: a wrong song in the folder is worse
/// than a track left marked unavailable.
/// </summary>
public sealed class YtDlpReplacementFinder(IProcessRunner runner, ILogger<YtDlpReplacementFinder> log) : IReplacementFinder
{
    private const int SearchResults = 6;
    private const int MaxProbes = 3;

    public async Task<ReplacementResult> FindAsync(ReplacementQuery query, DownloadOptions options, DownloadTools tools, CancellationToken ct)
    {
        var artist = ReplacementMatcher.DisplayArtist(query.Artist);
        var searchText = $"{artist} {query.Title}";

        var search = await RunAsync(YtDlpArgsBuilder.ReplacementSearch(searchText, SearchResults, options, tools.JsRuntime), tools, ct)
            .ConfigureAwait(false);
        if (!search.Succeeded)
        {
            var message = YtDlpErrorText.LastError(search.StandardError);
            log.LogWarning("Replacement search for {Id} ('{Query}') failed: {Error}", query.OriginalId, searchText, message);
            return new ReplacementResult(ReplacementOutcome.Error, Error: message);
        }

        var hits = ParseSearch(search.StandardOutput)
            .Where(h => h.Id != query.OriginalId && ReplacementMatcher.TitleMatches(query.Title, query.Artist, h.Title))
            .Take(MaxProbes)
            .ToList();
        log.LogDebug("Replacement search for {Id} ('{Query}'): {Count} hits with a matching title", query.OriginalId, searchText, hits.Count);

        foreach (var hit in hits)
        {
            var probe = await RunAsync(YtDlpArgsBuilder.VideoProbe(hit.Id, options, tools.JsRuntime), tools, ct).ConfigureAwait(false);
            if (!probe.Succeeded)
            {
                log.LogDebug("Replacement candidate {Candidate} for {Id} can't be read: {Error}",
                    hit.Id, query.OriginalId, YtDlpErrorText.LastError(probe.StandardError));
                continue;
            }
            if (ParseProbe(probe.StandardOutput) is not { } candidate) continue;

            if (ReplacementMatcher.Matches(query, candidate) is { } reason)
            {
                log.LogDebug("Replacement candidate {Candidate} for {Id} rejected: {Reason}", hit.Id, query.OriginalId, reason);
                continue;
            }

            var replacement = new Replacement(candidate.Id, candidate.Track ?? candidate.Title ?? hit.Title ?? "",
                candidate.Credit, candidate.DurationSec!.Value);
            log.LogInformation("Replacement for {Id} '{Title}': {Candidate} '{CandidateTitle}' by {Artists}",
                query.OriginalId, query.Title, replacement.VideoId, replacement.Title, replacement.Artists);
            return new ReplacementResult(ReplacementOutcome.Found, replacement);
        }

        log.LogInformation("No replacement found for {Id} '{Title}' by {Artist}", query.OriginalId, query.Title, query.Artist);
        return ReplacementResult.NotFound;
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> args, DownloadTools tools, CancellationToken ct)
    {
        var request = new ProcessRequest(tools.YtDlp.Path, args) { Environment = YtDlpArgsBuilder.Environment };
        try
        {
            return await runner.RunAsync(request, ct).ConfigureAwait(false);
        }
        catch (ProcessStartException ex)
        {
            return new ProcessResult(-1, "", "ERROR: " + ex.Message, TimeSpan.Zero);
        }
    }

    internal sealed record SearchHit(string Id, string? Title);

    /// <summary>Video entries of a flat search result, in result order.</summary>
    internal static List<SearchHit> ParseSearch(string json)
    {
        var hits = new List<SearchHit>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return hits;
            foreach (var e in entries.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var ieKey = Str(e, "ie_key");
                if (ieKey is not null && ieKey != "Youtube") continue;
                if (Str(e, "id") is { Length: > 0 } id) hits.Add(new SearchHit(id, Str(e, "title")));
            }
        }
        catch (JsonException)
        {
            // Treated as no hits.
        }
        return hits;
    }

    /// <summary>The fields of a single-video JSON that matching needs; null if it isn't one.</summary>
    internal static ReplacementCandidate? ParseProbe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || Str(e, "id") is not { Length: > 0 } id) return null;

            var artists = new List<string>();
            var credited = e.TryGetProperty("artists", out var credits) && credits.ValueKind == JsonValueKind.Array
                ? string.Join(", ", credits.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()))
                : null;
            foreach (var key in new[] { "artists", "creators" })
            {
                if (e.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
                    artists.AddRange(list.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!));
            }
            foreach (var key in new[] { "artist", "creator", "channel", "uploader" })
            {
                if (Str(e, key) is { Length: > 0 } a) artists.Add(a);
            }

            double? duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
            return new ReplacementCandidate(id, Str(e, "title"), Str(e, "track"),
                artists.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), duration, Str(e, "availability"))
            {
                Credit = credited is { Length: > 0 } ? credited : Str(e, "artist") ?? Str(e, "channel") ?? Str(e, "uploader") ?? "",
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <param name="Artists">Every artist / channel name the video lists.</param>
internal sealed record ReplacementCandidate(
    string Id, string? Title, string? Track, IReadOnlyList<string> Artists, double? DurationSec, string? Availability)
{
    /// <summary>The artists as YouTube Music credits them, for display.</summary>
    public string Credit { get; init; } = string.Join(", ", Artists);
}

/// <summary>Pure matching rules for replacements.</summary>
internal static partial class ReplacementMatcher
{
    /// <summary>Re-releases of the same recording differ by a second or two at most.</summary>
    public const double DurationToleranceSec = 3;

    /// <summary>Null when <paramref name="c"/> is the same song as <paramref name="q"/>, else why not.</summary>
    public static string? Matches(ReplacementQuery q, ReplacementCandidate c)
    {
        if (c.Id == q.OriginalId) return "same video";
        if (c.Availability is not (null or "public" or "unlisted")) return $"availability is {c.Availability}";
        if (c.DurationSec is not { } duration) return "no duration";
        if (Math.Abs(duration - q.DurationSec) > DurationToleranceSec)
            return $"length {duration:0}s vs {q.DurationSec:0}s";
        if (!TitleMatches(q.Title, q.Artist, c.Track) && !TitleMatches(q.Title, q.Artist, c.Title))
            return $"title '{c.Track ?? c.Title}'";
        if (!ArtistMatches(q.Artist, c.Artists)) return $"artists '{string.Join(", ", c.Artists)}'";
        return null;
    }

    /// <summary>"THz - Topic" → "THz" (auto-generated YouTube Music channels).</summary>
    public static string DisplayArtist(string artist)
    {
        var a = artist.Trim();
        return a.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) ? a[..^" - Topic".Length].TrimEnd() : a;
    }

    public static bool TitleMatches(string originalTitle, string originalArtist, string? candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(candidateTitle)) return false;
        var a = NormalizeTitle(originalTitle, originalArtist);
        return a.Length > 0 && a == NormalizeTitle(candidateTitle, originalArtist);
    }

    public static bool ArtistMatches(string originalArtist, IEnumerable<string> candidateArtists)
    {
        var a = Simplify(DisplayArtist(originalArtist));
        if (a.Length == 0) return false;
        foreach (var raw in candidateArtists)
        {
            foreach (var name in SplitArtists(DisplayArtist(raw)))
            {
                var c = Simplify(name);
                if (c.Length == 0) continue;
                if (c == a) return true;
                // "Mr. Bill & Of The Trees" listed as the uploader, credited as "Mr. Bill" (or the other way round).
                if (c.Length >= 3 && ContainsWord(a, c)) return true;
                if (a.Length >= 3 && ContainsWord(c, a)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Lower-case, accent-free words of the title without featured-artist credits, "(Official Audio)"-style suffixes
    /// and a leading "Artist - ". Version markers such as "(Live)" or "(Remix)" are kept, so they never match the original.
    /// </summary>
    public static string NormalizeTitle(string title, string? artist)
    {
        var s = NoiseGroups().Replace(title, " ");
        s = TrailingFeat().Replace(s, "");
        if (artist is not null)
        {
            var dash = s.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && Simplify(s[..dash]) == Simplify(DisplayArtist(artist))) s = s[(dash + 3)..];
        }
        return Simplify(s);
    }

    private static IEnumerable<string> SplitArtists(string s) =>
        ArtistSeparators().Split(s).Append(s);

    private static bool ContainsWord(string haystack, string word) =>
        (" " + haystack + " ").Contains(" " + word + " ", StringComparison.Ordinal);

    /// <summary>Lower-case letters and digits separated by single spaces, accents removed.</summary>
    internal static string Simplify(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = true;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"[\(\[]\s*(?:feat\.?|ft\.?|featuring|official\b[^\)\]]*|audio|lyrics?(?: video)?|visuali[sz]er|hq|hd)(?:\s[^\)\]]*)?[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoiseGroups();

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingFeat();

    [GeneratedRegex(@"\s*(?:,|&|\band\b|\bx\b|\bfeat\.?|\bft\.?)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtistSeparators();
}
