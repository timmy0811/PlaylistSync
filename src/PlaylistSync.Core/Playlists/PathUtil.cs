using PlaylistSync.Core.Sync;

namespace PlaylistSync.Core.Playlists;

/// <summary>Folder path helpers for the library (normalising user input, overlap checks).</summary>
public static class PathUtil
{
    /// <summary>
    /// Turns user input into a full path without a trailing separator. A leading <c>~</c> expands to
    /// <paramref name="home"/>. Returns null for empty or relative input (we never resolve against the
    /// process working directory) and for paths the OS rejects.
    /// </summary>
    public static string? NormalizeUserPath(string? input, string home)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var path = input.Trim();

        if (path == "~")
            path = home;
        else if (path.Length > 1 && path[0] == '~' && (path[1] == '/' || path[1] == Path.DirectorySeparatorChar))
            path = Path.Combine(home, path[2..]);

        if (!Path.IsPathFullyQualified(path)) return null;
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        // Keep a bare root ("/", "C:\") as is; strip the separator from everything else.
        return Path.GetPathRoot(path) == path ? path : Path.TrimEndingDirectorySeparator(path);
    }

    /// <summary>
    /// A folder name that is valid on Linux and Windows: reserved characters become <c>_</c>, trailing dots and
    /// spaces are trimmed, Windows device names get a suffix. Falls back to <paramref name="fallback"/> when nothing is left.
    /// </summary>
    public static string SafeFolderName(string? name, string fallback = "Playlist")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var chars = name.Trim().Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.', ' ');
        if (result.Length > 100) result = result[..100].TrimEnd('.', ' ');
        if (result.Length == 0 || result.All(c => c == '_' || c == '.')) return fallback;
        // Windows reserves device names even with an extension ("COM1.mix"), so the suffix goes on the stem.
        var dot = result.IndexOf('.');
        var stem = (dot < 0 ? result : result[..dot]).ToUpperInvariant();
        var reserved = stem is "CON" or "PRN" or "AUX" or "NUL" ||
                       (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3]));
        return reserved ? result.Insert(dot < 0 ? result.Length : dot, "_") : result;
    }

    /// <summary>True if the two folders are the same or one contains the other. Both must be normalised.</summary>
    public static bool Overlaps(string a, string b, StringComparer? comparer = null)
    {
        comparer ??= DiskSnapshot.PlatformComparer;
        return comparer.Equals(a, b) || IsInside(a, b, comparer) || IsInside(b, a, comparer);
    }

    private static bool IsInside(string child, string parent, StringComparer comparer)
    {
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return child.Length > prefix.Length && comparer.Equals(child[..prefix.Length], prefix);
    }
}
