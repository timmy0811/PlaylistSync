namespace PlaylistSync.Core.Playback;

/// <summary>
/// Finds the installed applications that play audio, from the freedesktop application folders
/// (<c>$XDG_DATA_HOME/applications</c>, then each <c>$XDG_DATA_DIRS/applications</c>). Linux / BSD only; empty on Windows.
/// </summary>
public static class PlayerCatalog
{
    /// <summary>Application folders in priority order (an id found earlier hides the same id later).</summary>
    public static IReadOnlyList<string> ApplicationDirs(Func<string, string?> getEnv, string home)
    {
        var dataHome = getEnv("XDG_DATA_HOME") is { Length: > 0 } dh && Path.IsPathRooted(dh)
            ? dh
            : Path.Combine(home, ".local", "share");
        var dataDirs = getEnv("XDG_DATA_DIRS") is { Length: > 0 } dd ? dd : "/usr/local/share:/usr/share";
        return new[] { dataHome }
            .Concat(dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathRooted))
            .Select(d => Path.Combine(d, "applications"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<string> ApplicationDirs() => OperatingSystem.IsWindows()
        ? []
        : ApplicationDirs(Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify));

    /// <summary>Visible applications that open audio files, sorted by name.</summary>
    public static IReadOnlyList<DesktopEntry> FindPlayers(IReadOnlyList<string> applicationDirs) =>
        Scan(applicationDirs)
            .Where(e => e.HandlesAudio)
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The entry with this desktop file id (e.g. <c>org.kde.haruna.desktop</c>), or null.</summary>
    public static DesktopEntry? Find(IReadOnlyList<string> applicationDirs, string id) =>
        Scan(applicationDirs).FirstOrDefault(e => e.Id == id);

    private static IEnumerable<DesktopEntry> Scan(IReadOnlyList<string> applicationDirs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in applicationDirs)
        {
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(dir)) continue;
                files = Directory.EnumerateFiles(dir, "*.desktop", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                // The id is the path below applications/ with '/' replaced by '-'.
                var id = Path.GetRelativePath(dir, file).Replace(Path.DirectorySeparatorChar, '-');
                if (!seen.Add(id)) continue; // a higher-priority folder already defined (or hid) it
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (DesktopEntry.Parse(id, file, text) is { } entry) yield return entry;
            }
        }
    }
}
