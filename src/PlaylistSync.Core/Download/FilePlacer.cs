namespace PlaylistSync.Core.Download;

/// <summary>
/// Moves a finished download from its per-track temp dir into the playlist folder.
/// Never overwrites: if the name is taken (another video with the same title, or a user file),
/// the video id is appended, then a counter.
/// <para>
/// When the temp dir is inside the playlist folder (the normal case) this is a rename. Otherwise (a network folder,
/// whose downloads happen in a local temp dir) the file is copied: .NET's own cross-device move fails on a kio-fuse
/// mount and leaves an empty target file behind, so it is never attempted.
/// </para>
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
        var copy = !IsInside(sourceRoot, targetRoot);

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
                if (copy)
                {
                    if (!CopyAcross(sourceFile, target)) continue;
                }
                else
                {
                    File.Move(sourceFile, target, overwrite: false);
                }
                return candidate.Replace('\\', '/');
            }
            catch (IOException) when (File.Exists(target) && attempt < 100)
            {
                // Lost a race with another writer; try the next name.
            }
        }
    }

    private static bool IsInside(string child, string parent)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(child));
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>
    /// Copies to a hidden <c>.part</c> file next to the target, renames it into place and deletes the source, so a
    /// half-copied file never shows under its real name. False if the target name got taken meanwhile (the source
    /// is then kept for the next name).
    /// </summary>
    private static bool CopyAcross(string sourceFile, string target)
    {
        var partial = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + ".part");
        try
        {
            using (var from = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var to = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                from.CopyTo(to, 1 << 20);
                to.Flush(flushToDisk: true);
            }
            if (File.Exists(target)) return false;
            File.Move(partial, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
        File.Delete(sourceFile);
        return true;
    }
}
