using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Sources;

namespace PlaylistSync.Core.Covers;

/// <summary>
/// Downloads playlist cover images once and keeps them in the app's data dir (<c>covers/</c>), one file per
/// playlist. The file name contains a hash of the URL, so a changed cover is fetched again and the old file removed.
/// Never throws for network or disk problems: the UI then simply shows a placeholder.
/// </summary>
public sealed class CoverCache(AppPaths paths, HttpClient http, ILogger<CoverCache> log) : ICoverStore
{
    /// <summary>Covers are a few hundred KB; anything bigger is not what we asked for.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public string Dir => Path.Combine(paths.DataDir, "covers");

    public string PathFor(Guid playlistId, string url) => Path.Combine(Dir, $"{Prefix(playlistId)}{Hash(url)}.img");

    /// <summary>Returns the cached file for <paramref name="url"/>, downloading it if needed. Null on any failure.</summary>
    public async Task<string?> GetAsync(Guid playlistId, string url, CancellationToken ct = default)
    {
        if (!PlaylistCover.IsAllowedHost(url))
        {
            log.LogDebug("Cover URL {Url} is not on a YouTube image host; ignored", url);
            return null;
        }

        var path = PathFor(playlistId, url);
        if (File.Exists(path)) return path;

        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) return path;
            var bytes = await DownloadAsync(url, ct).ConfigureAwait(false);
            if (bytes is null) return null;

            Directory.CreateDirectory(Dir);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            RemoveOthers(playlistId, keep: path);
            log.LogDebug("Cached cover of {Id} ({Bytes} bytes)", playlistId, bytes.Length);
            return path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            log.LogInformation("Could not fetch the cover {Url}: {Message}", url, ex.Message);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Deletes the cached cover of a playlist (when it is removed from the library).</summary>
    public void Remove(Guid playlistId) => RemoveOthers(playlistId, keep: null);

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            log.LogInformation("Cover {Url} returned HTTP {Status}", url, (int)response.StatusCode);
            return null;
        }
        if (response.Content.Headers.ContentType?.MediaType is not { } type || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("Cover {Url} is not an image ({Type})", url, response.Content.Headers.ContentType?.MediaType);
            return null;
        }
        if (response.Content.Headers.ContentLength > MaxBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    private void RemoveOthers(Guid playlistId, string? keep)
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var file in Directory.EnumerateFiles(Dir, Prefix(playlistId) + "*"))
            {
                if (keep is null || !string.Equals(file, keep, StringComparison.Ordinal)) File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Could not clean up old covers of {Id}", playlistId);
        }
    }

    private static string Prefix(Guid playlistId) => playlistId.ToString("N") + "-";

    private static string Hash(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)), 0, 8).ToLowerInvariant();
}
