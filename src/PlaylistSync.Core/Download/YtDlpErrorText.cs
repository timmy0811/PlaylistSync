namespace PlaylistSync.Core.Download;

/// <summary>Turns yt-dlp's stderr into a short, user-facing message.</summary>
internal static class YtDlpErrorText
{
    /// <summary>The last <c>ERROR:</c> line (or the last line at all), cleaned up.</summary>
    public static string LastError(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var errorLine = lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal))
                        ?? lines.LastOrDefault()
                        ?? "yt-dlp failed without an error message.";
        return Clean(errorLine);
    }

    /// <summary><c>ERROR: [youtube:tab] PLxyz: The playlist does not exist.</c> → <c>The playlist does not exist.</c></summary>
    public static string Clean(string line)
    {
        var s = line.StartsWith("ERROR:", StringComparison.Ordinal) ? line["ERROR:".Length..].TrimStart() : line;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close > 0) s = s[(close + 1)..].TrimStart();
            // Drop the "<id>: " prefix yt-dlp puts before the message.
            var colon = s.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && !s.AsSpan(0, colon).Contains(' ')) s = s[(colon + 2)..];
        }
        return s.Length > 0 ? s : line;
    }

    /// <summary>YouTube wants a signed-in session (bot check, age gate, members-only).</summary>
    public static bool NeedsCookies(string message) => ContainsAny(message,
        "confirm you", "not a bot", "Sign in", "sign in", "age-restricted", "members-only", "Join this channel", "cookies");

    public static bool ContainsAny(string s, params string[] needles) =>
        needles.Any(n => s.Contains(n, StringComparison.Ordinal));

    public const string CookiesHint = "YouTube requires a signed-in session for this. Set up cookies in Settings → Network.";
}
