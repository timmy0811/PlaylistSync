using PlaylistSync.Core.Locations;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Sources;
using PlaylistSync.Core.Sync;

namespace PlaylistSync.Core.Playlists;

/// <summary>Field errors for an add/edit form; null means the field is valid.</summary>
public sealed record PlaylistValidation(string? NameError, string? UrlError, string? DirectoryError, string? NormalizedDirectory)
{
    public bool IsValid => NameError is null && UrlError is null && DirectoryError is null;
}

/// <summary>Offline validation of a library entry. Pure: no IO.</summary>
public static class PlaylistValidator
{
    public const int MaxNameLength = 200;

    /// <param name="others">The library's entries; the one with <paramref name="editingId"/> is ignored.</param>
    public static PlaylistValidation Validate(
        string? name,
        string? url,
        string? directory,
        IEnumerable<PlaylistEntry> others,
        Guid? editingId,
        string home,
        StringComparer? pathComparer = null)
    {
        pathComparer ??= DiskSnapshot.PlatformComparer;

        string? nameError = null;
        if (string.IsNullOrWhiteSpace(name))
            nameError = "Enter a name.";
        else if (name.Trim().Length > MaxNameLength)
            nameError = $"The name is too long (max {MaxNameLength} characters).";

        string? urlError = null;
        if (string.IsNullOrWhiteSpace(url))
            urlError = "Enter a playlist URL.";
        else if (!YouTubeUrl.IsYouTube(url))
            urlError = "Only YouTube and YouTube Music playlist URLs are supported.";

        string? dirError = null;
        var normalized = PathUtil.NormalizeUserPath(directory, home);
        var remote = RemoteAddress.Parse(normalized);
        if (string.IsNullOrWhiteSpace(directory))
            dirError = "Choose a folder.";
        else if (normalized is null && RemoteAddress.LooksLikeUri(directory))
            dirError = OperatingSystem.IsWindows()
                ? "Enter a network folder as smb://server/share/folder (or \\\\server\\share\\folder)."
                : "Enter a network folder as smb://[user@]server/share/folder (sftp://, ftp:// and webdav(s):// work too).";
        else if (normalized is null)
            dirError = "Enter a full folder path.";
        else if (remote is { IsRoot: true })
            dirError = "Choose a folder inside the share, not the share itself.";
        else if (remote is null && Path.GetPathRoot(normalized) == normalized)
            dirError = "Choose a folder, not a drive root.";
        else if (pathComparer.Equals(normalized, PathUtil.NormalizeUserPath(home, home)))
            dirError = "Choose a dedicated folder, not your home folder.";
        else
        {
            foreach (var other in others)
            {
                if (other.Id == editingId) continue;
                var otherDir = PathUtil.NormalizeUserPath(other.Directory, home);
                if (otherDir is null || !PathUtil.Overlaps(normalized, otherDir, pathComparer)) continue;
                dirError = pathComparer.Equals(normalized, otherDir)
                    ? $"This folder is already used by '{other.Name}'."
                    : $"This folder overlaps with the folder of '{other.Name}'. Each playlist needs its own folder.";
                break;
            }
        }

        return new PlaylistValidation(nameError, urlError, dirError, normalized);
    }
}
