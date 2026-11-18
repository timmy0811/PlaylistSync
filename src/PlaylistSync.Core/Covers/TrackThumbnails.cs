using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Covers;

/// <summary>Small per-track images for the track list.</summary>
public interface ITrackThumbnails
{
    /// <summary>
    /// Turns the video thumbnail yt-dlp left in <paramref name="tempDir"/> into a small JPEG of <paramref name="shape"/>
    /// for <paramref name="trackId"/>. Best effort: returns false (and logs) when there is none or ffmpeg fails.
    /// </summary>
    Task<bool> CreateFromTempDirAsync(string trackId, string tempDir, ToolLocation ffmpeg, CancellationToken ct,
        ThumbnailShape shape = ThumbnailShape.Square);
}

/// <summary>Square for music tracks (like album art), 16:9 for videos.</summary>
public enum ThumbnailShape { Square, Wide }

/// <summary>Where a backfilled thumbnail came from.</summary>
public enum ThumbnailSource
{
    /// <summary>Nothing worked (no cover in the file, YouTube has no image, or ffmpeg failed).</summary>
    None,
    /// <summary>The thumbnail already existed.</summary>
    Existing,
    /// <summary>The cover embedded in the audio file.</summary>
    AudioFile,
    /// <summary>YouTube's small video thumbnail (one image request, the audio is not downloaded).</summary>
    YouTube,
}

/// <summary>
/// Track thumbnails in the app's data dir (<c>thumbnails/&lt;video id&gt;.jpg</c>), not in the music folder, so players
/// don't pick them up and network folders get no extra files. Keyed by the playlist's video id (the track identity, also
/// for replacements), so the same video in two playlists shares one file and a re-added folder finds its images again.
/// </summary>
/// <param name="http">For <see cref="CreateMissingAsync"/>'s YouTube fallback; without it only embedded covers are used.</param>
public sealed partial class TrackThumbnails(
    AppPaths paths,
    IProcessRunner runner,
    ILogger<TrackThumbnails> log,
    HttpClient? http = null) : ITrackThumbnails
{
    /// <summary>Edge length in pixels: the list shows 34 px, so this stays sharp up to 2.5× scaling (a few KB each).</summary>
    public const int Size = 96;

    /// <summary>Width of a <see cref="ThumbnailShape.Wide"/> (16:9) thumbnail; its height is <see cref="Size"/>.</summary>
    public const int WideWidth = 170;

    public static ThumbnailShape ShapeFor(PlaylistKind kind) => kind == PlaylistKind.Video ? ThumbnailShape.Wide : ThumbnailShape.Square;

    /// <summary>File name (without extension) yt-dlp writes the thumbnail to inside the per-track temp dir.</summary>
    public const string TempFileStem = "thumbnail";

    /// <summary>YouTube's thumbnails are tens of KB; anything bigger is not what we asked for.</summary>
    private const int MaxDownloadBytes = 2 * 1024 * 1024;

    /// <summary>Backfill works in the background while the list scrolls; keep it from hogging the CPU or a NAS.</summary>
    private const int MaxParallelBackfills = 2;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>ffmpeg on one image (or the tag of one audio file) takes well under a second; this only guards against a hang.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _backfillGate = new(MaxParallelBackfills);

    /// <summary>Tracks (id and shape) for which nothing worked; not tried again until the app restarts.</summary>
    private readonly ConcurrentDictionary<string, byte> _gaveUp = new(StringComparer.Ordinal);

    public string Dir => Path.Combine(paths.DataDir, "thumbnails");

    /// <summary>
    /// Track ids come from manifests, which are files in the user's folders; only plain ids may become file names or
    /// URL parts. YouTube ids are <c>[A-Za-z0-9_-]</c>.
    /// </summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    /// <summary>
    /// Where the thumbnail of a track lives: <c>&lt;id&gt;.jpg</c> (square) or <c>&lt;id&gt;.wide.jpg</c> (16:9), so the
    /// same video in a music and a video playlist gets both. (On a case-insensitive file system two ids that differ only
    /// in case would share a file; with 11-character ids that is not worth guarding against.)
    /// </summary>
    public string PathFor(string trackId, ThumbnailShape shape = ThumbnailShape.Square)
    {
        if (!IsValidId(trackId)) throw new ArgumentException($"'{trackId}' is not a video id.", nameof(trackId));
        return Path.Combine(Dir, trackId + (shape == ThumbnailShape.Wide ? ".wide.jpg" : ".jpg"));
    }

    /// <summary>The thumbnail file if one exists.</summary>
    public string? Find(string trackId, ThumbnailShape shape = ThumbnailShape.Square)
    {
        if (!IsValidId(trackId)) return null;
        var path = PathFor(trackId, shape);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The image yt-dlp wrote with <c>-o thumbnail:thumbnail.%(ext)s</c> (converted or not), if any.</summary>
    internal static string? FindSource(string tempDir)
    {
        try
        {
            return Directory.EnumerateFiles(tempDir, TempFileStem + ".*")
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => Array.FindIndex(ImageExtensions, e => e.Equals(Path.GetExtension(f), StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// ffmpeg arguments: centre crop to <paramref name="shape"/> (square, or 16:9 for a pillar/letterboxed image),
    /// scaled down, one JPEG frame. <paramref name="fromAudioFile"/> picks the embedded cover (the first video stream);
    /// without one ffmpeg fails.
    /// </summary>
    internal static IReadOnlyList<string> FfmpegArgs(string source, string target, bool fromAudioFile = false,
        ThumbnailShape shape = ThumbnailShape.Square)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", source };
        if (fromAudioFile) args.AddRange(["-map", "0:v:0"]);
        var filter = shape == ThumbnailShape.Wide
            ? $"crop='min(iw,ih*16/9)':'min(ih,iw*9/16)',scale={WideWidth}:{Size}:flags=lanczos"
            : $"crop='min(iw,ih)':'min(iw,ih)',scale={Size}:{Size}:flags=lanczos";
        args.AddRange(
        [
            "-vf", filter,
            "-frames:v", "1",
            "-q:v", "4",
            "-f", "mjpeg",
            target,
        ]);
        return args;
    }

    /// <summary>YouTube's 320×180 thumbnail: 16:9 without the letterbox bars of <c>hqdefault</c>.</summary>
    internal static string YouTubeThumbnailUrl(string videoId) => $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

    public async Task<bool> CreateFromTempDirAsync(string trackId, string tempDir, ToolLocation ffmpeg, CancellationToken ct,
        ThumbnailShape shape = ThumbnailShape.Square)
    {
        if (!IsValidId(trackId)) return false;
        if (FindSource(tempDir) is not { } source)
        {
            log.LogDebug("No thumbnail for {Id} in {Dir}", trackId, tempDir);
            return false;
        }
        return await ConvertAsync(trackId, source, fromAudioFile: false, shape, ffmpeg, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes the thumbnail of a track downloaded before thumbnails existed (or of a video seen first in a playlist of
    /// the other kind), without downloading the track again:
    /// first from the cover embedded in <paramref name="audioPath"/>, else from YouTube's small thumbnail of
    /// <paramref name="sourceVideoId"/> (the replacement, if the file came from one). Runs at most
    /// <see cref="MaxParallelBackfills"/> at a time; a track for which nothing works is not tried again this session.
    /// Never throws, except on cancellation.
    /// </summary>
    /// <param name="audioPath">The audio file; pass null for a video file (its first video stream is the video itself).</param>
    public async Task<ThumbnailSource> CreateMissingAsync(
        string trackId, string? audioPath, string? sourceVideoId, ToolLocation ffmpeg, CancellationToken ct,
        ThumbnailShape shape = ThumbnailShape.Square)
    {
        var key = shape == ThumbnailShape.Wide ? trackId + ".wide" : trackId;
        if (!IsValidId(trackId) || _gaveUp.ContainsKey(key)) return ThumbnailSource.None;
        if (Find(trackId, shape) is not null) return ThumbnailSource.Existing;

        await _backfillGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller (or the download itself) may have made it while this one waited.
            if (Find(trackId, shape) is not null) return ThumbnailSource.Existing;

            if (audioPath is not null && FileExists(audioPath)
                && await ConvertAsync(trackId, audioPath, fromAudioFile: true, shape, ffmpeg, ct).ConfigureAwait(false))
            {
                return ThumbnailSource.AudioFile;
            }

            var videoId = IsValidId(sourceVideoId) ? sourceVideoId! : trackId;
            if (await FromYouTubeAsync(trackId, videoId, shape, ffmpeg, ct).ConfigureAwait(false)) return ThumbnailSource.YouTube;

            _gaveUp[key] = 0;
            return ThumbnailSource.None;
        }
        finally
        {
            _backfillGate.Release();
        }
    }

    private async Task<bool> FromYouTubeAsync(string trackId, string videoId, ThumbnailShape shape, ToolLocation ffmpeg, CancellationToken ct)
    {
        if (http is null) return false;
        var download = Path.Combine(Dir, $"{trackId}.{Guid.NewGuid():N}.src.tmp");
        try
        {
            using var response = await http.GetAsync(YouTubeThumbnailUrl(videoId), HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            // A gone video gets a 404 (with a grey placeholder image as the body).
            if (!response.IsSuccessStatusCode)
            {
                log.LogDebug("No YouTube thumbnail for {Id} (HTTP {Status})", videoId, (int)response.StatusCode);
                return false;
            }
            if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
                || response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                return false;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length is 0 or > MaxDownloadBytes) return false;

            Directory.CreateDirectory(Dir);
            await File.WriteAllBytesAsync(download, bytes, ct).ConfigureAwait(false);
            return await ConvertAsync(trackId, download, fromAudioFile: false, shape, ffmpeg, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            log.LogDebug("Could not fetch the YouTube thumbnail of {Id}: {Message}", videoId, ex.Message);
            return false;
        }
        finally
        {
            TryDelete(download);
        }
    }

    private async Task<bool> ConvertAsync(string trackId, string source, bool fromAudioFile, ThumbnailShape shape,
        ToolLocation ffmpeg, CancellationToken ct)
    {
        var target = PathFor(trackId, shape);
        // Unique, so a download and a backfill of the same track never write the same temp file.
        var tmp = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Dir);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            ProcessResult result;
            try
            {
                result = await runner.RunAsync(new ProcessRequest(ffmpeg.Path, FfmpegArgs(source, tmp, fromAudioFile, shape)), timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log.LogInformation("ffmpeg took too long making the thumbnail of {Id}", trackId);
                return false;
            }
            if (!result.Succeeded || !File.Exists(tmp))
            {
                // Expected for audio files without a cover ("matches no streams"), so not worth more than debug.
                log.Log(fromAudioFile ? LogLevel.Debug : LogLevel.Information,
                    "Could not make the thumbnail of {Id} from {Source}: {Error}", trackId, source, result.StandardError.Trim());
                return false;
            }
            File.Move(tmp, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProcessStartException)
        {
            log.LogInformation(ex, "Could not make the thumbnail of {Id}", trackId);
            return false;
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers are a few KB; not worth failing anything over.
        }
    }
}
