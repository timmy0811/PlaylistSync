using System.Globalization;
using System.Text;

namespace PlaylistSync.Core.Playback;

/// <summary>One entry of a play queue: an absolute file path plus optional display info.</summary>
public sealed record QueueItem(string Path, string? Title = null, double? DurationSec = null);

/// <summary>
/// Builds extended M3U play queues. Opening one with the system's default handler lets any player (Haruna, mpv, VLC,
/// Windows Media Player…) play the tracks in exactly this order, e.g. shuffled, without the app knowing the player.
/// </summary>
public static class M3uPlaylist
{
    /// <summary>The M3U text for <paramref name="items"/> (UTF-8, "\n" line endings, absolute paths).</summary>
    public static string Build(IEnumerable<QueueItem> items)
    {
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var item in items)
        {
            var seconds = item.DurationSec is { } d && d > 0 ? (long)Math.Round(d) : -1;
            var title = item.Title ?? System.IO.Path.GetFileNameWithoutExtension(item.Path);
            // Line breaks would end the directive early.
            title = title.Replace('\r', ' ').Replace('\n', ' ');
            sb.Append("#EXTINF:").Append(seconds.ToString(CultureInfo.InvariantCulture)).Append(',').Append(title).Append('\n');
            sb.Append(UnrememberedPath(item.Path)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The same file written as <c>&lt;dir&gt;/./&lt;name&gt;</c>. Haruna starts an opened .m3u at the entry whose path
    /// equals its remembered <c>LastPlayedFile</c> (compared as written, while it stores the cleaned path), so without
    /// this the queue would start at the track played last instead of the first entry. Every player and file system
    /// resolves "." to the folder itself, so the file is the same.
    /// </summary>
    internal static string UnrememberedPath(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return path;
        return System.IO.Path.Combine(dir, ".", System.IO.Path.GetFileName(path));
    }

    /// <summary>A shuffled copy of <paramref name="items"/> (Fisher–Yates).</summary>
    public static IReadOnlyList<T> Shuffle<T>(IEnumerable<T> items, Random random)
    {
        var list = items.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}
