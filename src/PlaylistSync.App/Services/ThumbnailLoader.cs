using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Covers;
using PlaylistSync.Core.Locations;
using PlaylistSync.Core.Sync;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.App.Services;

/// <summary>
/// Decodes the small track images (<see cref="TrackThumbnails"/>) for the track list, and makes the missing ones of
/// tracks downloaded before thumbnails existed (from the file's embedded cover, else YouTube's image; never by
/// downloading the track again).
/// </summary>
public sealed class ThumbnailLoader(
    TrackThumbnails store,
    ToolLocator tools,
    SettingsService settings,
    FolderResolver folders,
    ILogger<ThumbnailLoader> log)
{
    /// <summary>The list shows 34 px high; decoding at 2× keeps it sharp on HiDPI while staying ≈20 KB per row.</summary>
    private const int DecodeWidth = 72;
    /// <summary>16:9 thumbnails of video playlists are 60 px wide in the list.</summary>
    private const int WideDecodeWidth = 120;

    /// <summary>Folders that could not be resolved (NAS off): not tried again for thumbnails, so no repeated mount attempts.</summary>
    private readonly ConcurrentDictionary<string, byte> _unavailableFolders = new(StringComparer.Ordinal);

    /// <summary>Null if the track has no thumbnail or it can't be decoded. Never throws.</summary>
    public Task<Bitmap?> LoadAsync(string trackId, ThumbnailShape shape = ThumbnailShape.Square) => Task.Run(() =>
    {
        if (store.Find(trackId, shape) is not { } path) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, shape == ThumbnailShape.Wide ? WideDecodeWidth : DecodeWidth,
                BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not decode the thumbnail {Path}", path);
            return null;
        }
    });

    /// <summary>
    /// Makes the thumbnail of a downloaded track that has none. <paramref name="file"/> is relative to the playlist
    /// folder <paramref name="directory"/>; <paramref name="sourceVideoId"/> is the replacement the file came from, if
    /// any. A video file is not looked into (its first video stream is the video, not a cover): its thumbnail comes from
    /// YouTube. True if a thumbnail exists afterwards. Never throws.
    /// </summary>
    public Task<bool> CreateMissingAsync(string trackId, string directory, string file, string? sourceVideoId,
        ThumbnailShape shape = ThumbnailShape.Square) => Task.Run(async () =>
    {
        try
        {
            if (tools.Locate(ToolKind.Ffmpeg, settings.Current.Tools) is not { Exists: true } ffmpeg) return false;
            var audioPath = shape == ThumbnailShape.Wide ? null : await AudioPathAsync(directory, file).ConfigureAwait(false);
            var source = await store.CreateMissingAsync(trackId, audioPath, sourceVideoId, ffmpeg, CancellationToken.None, shape)
                .ConfigureAwait(false);
            if (source is ThumbnailSource.AudioFile or ThumbnailSource.YouTube)
                log.LogDebug("Made the missing thumbnail of {Id} from {Source}", trackId, source);
            return source != ThumbnailSource.None;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not make the thumbnail of {Id}", trackId);
            return false;
        }
    });

    /// <summary>The audio file's full path; null if the folder can't be reached (then only YouTube's image is tried).</summary>
    private async Task<string?> AudioPathAsync(string directory, string file)
    {
        if (_unavailableFolders.ContainsKey(directory)) return null;
        try
        {
            var dir = (await folders.ResolveAsync(directory).ConfigureAwait(false)).Path;
            return TrackList.ResolveFile(dir, file);
        }
        catch (FolderUnavailableException)
        {
            _unavailableFolders[directory] = 0;
            return null;
        }
    }
}
