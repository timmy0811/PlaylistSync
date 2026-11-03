namespace PlaylistSync.Core.Download;

/// <summary>
/// Moves a finished download from its per-track temp dir into the playlist folder.
/// Never overwrites: if the name is taken (another video with the same title, or a user file),
/// the video id is appended, then a counter.
/// </summary>
public static class FilePlacer
{
    /// <returns>The path relative to <paramref name="targetRoot"/>, with <c>/</c> separators.</returns>
    /// <exception cref="IOException">The file could not be moved.</exception>
    public static string MoveIntoPlace(string sourceFile, string sourceRoot, string targetRoot, string videoId, bool restrictFilenames)
    {
        var relative = Path.GetRelativePath(sourceRoot, sourceFile);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new IOException($"yt-dlp wrote the file outside its temp folder: '{sourceFile}'.");

        var relDir = Path.GetDirectoryName(relative) ?? "";
        var stem = Path.GetFileNameWithoutExtension(relative);
        var ext = Path.GetExtension(relative);
        var idSuffix = restrictFilenames ? "_" + videoId : $" [{videoId}]";

        Directory.CreateDirectory(Path.Combine(targetRoot, relDir));

        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 1000) throw new IOException($"No free file name for '{relative}' in '{targetRoot}'.");
            var name = attempt switch
            {
                0 => stem + ext,
                1 => stem + idSuffix + ext,
                _ => $"{stem}{idSuffix}{(restrictFilenames ? "_" : " (")}{attempt}{(restrictFilenames ? "" : ")")}{ext}",
            };
            var candidate = Path.Combine(relDir, name);
            var target = Path.Combine(targetRoot, candidate);
            if (File.Exists(target) || Directory.Exists(target)) continue;
            try
            {
                File.Move(sourceFile, target, overwrite: false);
                return candidate.Replace('\\', '/');
            }
            catch (IOException) when (File.Exists(target) && attempt < 100)
            {
                // Lost a race with another writer; try the next name.
            }
        }
    }
}
