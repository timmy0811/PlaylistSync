using System.Text;

namespace PlaylistSync.Core.Storage;

public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes to <c>path.tmp</c>, flushes it to disk, then renames it over <paramref name="path"/>.
    /// A crash mid-write leaves the previous file intact.
    /// </summary>
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 4096, FileOptions.Asynchronous))
        {
            var bytes = Utf8NoBom.GetBytes(content);
            await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Renames a broken file aside (<c>name.corrupt-yyyyMMddHHmmss</c>) and returns the new path.</summary>
    public static string MoveAside(string path)
    {
        var target = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
        File.Move(path, target, overwrite: true);
        return target;
    }
}
