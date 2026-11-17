namespace PlaylistSync.Core.Covers;

/// <summary>Source of cached playlist cover files (implemented by <see cref="CoverCache"/>).</summary>
public interface ICoverStore
{
    /// <summary>Local path of the cover image for <paramref name="url"/>, fetching it if needed. Null on any failure.</summary>
    Task<string?> GetAsync(Guid playlistId, string url, CancellationToken ct = default);
}

public enum FolderCoverResult
{
    Written,
    /// <summary>The folder already has a cover image (ours from an earlier sync, or the user's). It is never replaced.</summary>
    AlreadyPresent,
    /// <summary>The image is neither JPEG nor PNG, which is what music players look for.</summary>
    UnsupportedFormat,
}

/// <summary>
/// Saves the playlist cover into the playlist folder as <c>cover.jpg</c> / <c>cover.png</c>, the name music players
/// look for. Never overwrites: if any usual cover file exists, nothing is written.
/// </summary>
public static class FolderCover
{
    /// <summary>File names music players pick up as the folder image (compared case-insensitively).</summary>
    public static readonly IReadOnlyList<string> KnownNames =
    [
        "cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.jpeg", "folder.png",
        "front.jpg", "front.jpeg", "front.png", "album.jpg", "album.png",
    ];

    /// <summary>The existing cover file in <paramref name="dir"/>, or null.</summary>
    public static string? FindExisting(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir)
            .FirstOrDefault(f => KnownNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>".jpg" or ".png" from the file's magic bytes; null for anything else (e.g. WebP).</summary>
    public static string? DetectExtension(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ".jpg";
        if (header.Length >= 8 && header[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return ".png";
        return null;
    }

    /// <summary>Copies <paramref name="imageFile"/> into <paramref name="dir"/> as cover.jpg/png unless a cover exists.</summary>
    public static async Task<FolderCoverResult> SaveAsync(string dir, string imageFile, CancellationToken ct = default)
    {
        if (FindExisting(dir) is not null) return FolderCoverResult.AlreadyPresent;

        var bytes = await File.ReadAllBytesAsync(imageFile, ct).ConfigureAwait(false);
        if (DetectExtension(bytes) is not { } ext) return FolderCoverResult.UnsupportedFormat;

        var target = Path.Combine(dir, "cover" + ext);
        var tmp = Path.Combine(dir, $".cover-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            // overwrite: false, so a cover that appeared in the meantime still wins.
            File.Move(tmp, target, overwrite: false);
            return FolderCoverResult.Written;
        }
        catch (IOException) when (File.Exists(target))
        {
            return FolderCoverResult.AlreadyPresent;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
