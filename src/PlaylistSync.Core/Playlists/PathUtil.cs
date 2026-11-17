using PlaylistSync.Core.Locations;
using PlaylistSync.Core.Sync;

namespace PlaylistSync.Core.Playlists;

/// <summary>Folder path helpers for the library (normalising user input, overlap checks).</summary>
public static class PathUtil
{
    /// <summary>
    /// Turns user input into a full path without a trailing separator. A leading <c>~</c> expands to
    /// <paramref name="home"/>. Returns null for empty or relative input (we never resolve against the
    /// process working directory) and for paths the OS rejects.
    /// <para>
    /// Network addresses (<c>smb://user@nas/share/folder</c>, see <see cref="RemoteAddress"/>) are kept as addresses
    /// in their canonical form; on Windows an SMB address becomes a UNC path. A kio-fuse / gvfs mount path (what a
    /// file dialog may return for a network folder) is turned back into its address. <c>file://</c> URIs become paths.
    /// </para>
    /// </summary>
    public static string? NormalizeUserPath(string? input, string home)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var path = input.Trim();

        if (RemoteAddress.LooksLikeUri(path))
        {
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(path, UriKind.Absolute, out var fileUri) || !fileUri.IsFile) return null;
                path = fileUri.LocalPath;
            }
            else
            {
                var address = RemoteAddress.Parse(path);
                if (address is null) return null;
                if (!OperatingSystem.IsWindows()) return address.ToString();
                path = address.ToUnc();
                if (path is null) return null;
            }
        }
        else if (RemoteAddress.FromMountPath(path) is { } mounted)
        {
            return mounted.ToString();
        }

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

    /// <summary>The user's home folder.</summary>
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The system Music folder (<c>~/Music</c> when the OS does not report one).</summary>
    public static string MusicDir
    {
        get
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            return string.IsNullOrEmpty(music) ? Path.Combine(Home, "Music") : music;
        }
    }

    /// <summary>
    /// The folder new playlists go into: the configured default folder (see <c>AppSettings.DefaultDirectory</c>), or
    /// <paramref name="musicDir"/> when it is empty or not a usable full path.
    /// </summary>
    public static string BaseDirectory(string? configured, string home, string musicDir) =>
        NormalizeUserPath(configured, home) ?? musicDir;

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
