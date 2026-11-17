using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Covers;

namespace PlaylistSync.App.Services;

/// <summary>Turns a cached playlist cover into a small bitmap for the sidebar and header.</summary>
public sealed class CoverLoader(CoverCache cache, ILogger<CoverLoader> log)
{
    /// <summary>Decode width: the header shows 72 px, so this stays sharp at 2× scaling and for 16:9 fallbacks.</summary>
    private const int DecodeWidth = 256;

    /// <summary>Null if the cover can't be fetched or decoded. Never throws (except on cancellation).</summary>
    public async Task<Bitmap?> LoadAsync(Guid playlistId, string url, CancellationToken ct = default)
    {
        var path = await cache.GetAsync(playlistId, url, ct).ConfigureAwait(false);
        if (path is null) return null;
        return await Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception ex)
            {
                log.LogInformation(ex, "Could not decode the cover {Path}", path);
                return null;
            }
        }, ct).ConfigureAwait(false);
    }

    public void Remove(Guid playlistId) => cache.Remove(playlistId);
}
