using System.Globalization;
using System.Text;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Storage;

namespace PlaylistSync.Core.Sync;

/// <summary>Writes <c>&lt;name&gt;.m3u8</c> into the playlist folder, in remote order.</summary>
public static class M3uWriter
{
    /// <summary>File name for the playlist, safe on every platform.</summary>
    public static string FileName(string playlistName)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var name = new string(playlistName.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        return (name.Length == 0 ? "playlist" : name) + ".m3u8";
    }

    /// <summary>
    /// Builds the content: tracks still in the remote playlist that have a file (synced, or unavailable but kept),
    /// ordered by remote position. Removed-in-remote and missing files are left out.
    /// </summary>
    public static string Build(Manifest manifest)
    {
        var sb = new StringBuilder("#EXTM3U\n");
        var tracks = manifest.Tracks.Values
            .Where(t => t.File is not null && t.RemoteIndex is not null && t.Status is TrackStatus.Synced or TrackStatus.Unavailable)
            .OrderBy(t => t.RemoteIndex);
        foreach (var t in tracks)
        {
            var seconds = t.DurationSec is { } d ? (int)Math.Round(d) : -1;
            // The YouTube title only: it usually names the artist already, and the uploader is often a label.
            var display = t.Title ?? Path.GetFileNameWithoutExtension(t.File!);
            sb.Append("#EXTINF:").Append(seconds.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(display.ReplaceLineEndings(" ")).Append('\n')
              .Append(t.File!.Replace('/', Path.DirectorySeparatorChar)).Append('\n');
        }
        return sb.ToString();
    }

    public static Task WriteAsync(string directory, string playlistName, Manifest manifest, CancellationToken ct = default) =>
        AtomicFile.WriteAllTextAsync(Path.Combine(directory, FileName(playlistName)), Build(manifest), ct);
}
