using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Locations;

/// <summary>A network folder can't be reached (server off, not logged in, no mount helper installed …).</summary>
public sealed class FolderUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

/// <param name="Path">A path the OS can open (for a network folder: inside its kio-fuse / gvfs mount, or UNC).</param>
/// <param name="IsRemote">The library entry is a network address.</param>
public sealed record ResolvedFolder(string Path, bool IsRemote);

/// <summary>
/// Turns a playlist folder from the library into a local path. Local paths pass through. Network addresses
/// (<c>smb://…</c>, see <see cref="RemoteAddress"/>) are mounted on demand: on Linux through kio-fuse (KDE, the way
/// Dolphin hands such files to other apps) or gvfs (<c>gio</c>, GNOME); on Windows an SMB address becomes a UNC path.
/// Credentials come from the desktop's own store (KWallet / GNOME Keyring / Windows); it may show its login prompt.
/// The library keeps the address, since the mount path changes between sessions.
/// </summary>
public sealed class FolderResolver(IProcessRunner runner, ILogger<FolderResolver> log, string remoteTempBase)
{
    private static readonly TimeSpan MountTimeout = TimeSpan.FromMinutes(2); // long enough to type a password

    /// <summary>Resolves local paths only; network addresses fail. For tests and tools that don't need them.</summary>
    public static FolderResolver LocalOnly { get; } =
        new(new ProcessRunner(), NullLogger<FolderResolver>.Instance, Path.Combine(Path.GetTempPath(), "playlist-sync-downloads"))
        {
            _localOnly = true,
        };

    private bool _localOnly;
    private readonly ConcurrentDictionary<string, string> _mounts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mountLock = new(1, 1);

    public static bool IsRemote(string? directory) => RemoteAddress.LooksLikeUri(directory);

    /// <summary>
    /// Downloads for a network folder go to a local temp folder first (yt-dlp and ffmpeg work on local files);
    /// only the finished file is copied over. One per address, so a cancelled download can resume.
    /// </summary>
    public string TempRootFor(string directory)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))[..16].ToLowerInvariant();
        return Path.Combine(remoteTempBase, hash);
    }

    /// <exception cref="FolderUnavailableException">The network folder can't be reached.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public async Task<ResolvedFolder> ResolveAsync(string directory, CancellationToken ct = default)
    {
        if (!IsRemote(directory)) return new ResolvedFolder(directory, false);

        var address = RemoteAddress.Parse(directory)
                      ?? throw new FolderUnavailableException($"'{directory}' is not a supported network address.");
        if (_localOnly) throw new FolderUnavailableException($"Network folders are not available here ({address}).");

        if (OperatingSystem.IsWindows())
        {
            return address.ToUnc() is { } unc
                ? new ResolvedFolder(unc, true)
                : throw new FolderUnavailableException($"Only smb:// network folders are supported on Windows ({address}).");
        }

        var root = address.MountRoot;
        var mounted = await MountAsync(root, ct).ConfigureAwait(false);
        return new ResolvedFolder(address.SubPath.Aggregate(mounted, Path.Combine), true);
    }

    /// <summary>The local path of a mount root, from the cache if it is still there.</summary>
    private async Task<string> MountAsync(RemoteAddress root, CancellationToken ct)
    {
        var key = root.ToString();
        if (_mounts.TryGetValue(key, out var cached) && Directory.Exists(cached)) return cached;

        // One at a time: on start-up every playlist on the same share asks at once, and only one login prompt should show.
        await _mountLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_mounts.TryGetValue(key, out cached) && Directory.Exists(cached)) return cached;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(MountTimeout + TimeSpan.FromSeconds(10));
            string path;
            try
            {
                path = await MountWithKioFuseAsync(root, timeout.Token).ConfigureAwait(false)
                       ?? await MountWithGioAsync(root, timeout.Token).ConfigureAwait(false)
                       ?? throw new FolderUnavailableException(
                           "Network folders need kio-fuse (KDE) or gvfs with gvfs-smb (GNOME). Install one of them, " +
                           "or mount the share yourself and choose the mounted folder.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new FolderUnavailableException($"Connecting to {root} timed out.");
            }

            if (!Directory.Exists(path))
                throw new FolderUnavailableException($"{root} was mounted at '{path}', but that folder can't be opened.");
            log.LogInformation("Mounted {Root} at {Path}", root, path);
            _mounts[key] = path;
            return path;
        }
        finally
        {
            _mountLock.Release();
        }
    }

    /// <summary>Null if kio-fuse is not installed.</summary>
    private async Task<string?> MountWithKioFuseAsync(RemoteAddress root, CancellationToken ct)
    {
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(new ProcessRequest("busctl",
            [
                "--user", "--json=short", $"--timeout={(int)MountTimeout.TotalSeconds}",
                "call", "org.kde.KIOFuse", "/org/kde/KIOFuse", "org.kde.KIOFuse.VFS", "mountUrl", "s", root.ToUriString(),
            ]), ct).ConfigureAwait(false);
        }
        catch (ProcessStartException ex)
        {
            log.LogDebug(ex, "busctl is not available");
            return null;
        }

        if (!result.Succeeded)
        {
            var error = result.StandardError.Trim();
            if (error.Contains("not provided by any .service", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("ServiceUnknown", StringComparison.OrdinalIgnoreCase))
            {
                log.LogDebug("kio-fuse is not installed: {Error}", error);
                return null;
            }
            log.LogWarning("kio-fuse could not mount {Root}: {Error}", root, error);
            throw new FolderUnavailableException($"Could not connect to {root}: {CleanBusctlError(error)}");
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var path = doc.RootElement.GetProperty("data")[0].GetString();
            if (!string.IsNullOrEmpty(path)) return path;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            log.LogWarning(ex, "Unexpected kio-fuse reply: {Output}", result.StandardOutput);
        }
        throw new FolderUnavailableException($"Could not connect to {root}: kio-fuse gave no folder.");
    }

    /// <summary>Null if gio (or its SMB backend) is not installed.</summary>
    private async Task<string?> MountWithGioAsync(RemoteAddress root, CancellationToken ct)
    {
        var uri = root.ToUriString();
        ProcessResult mount;
        try
        {
            // Fails harmlessly with "already mounted"; the info call below tells whether it worked.
            mount = await runner.RunAsync(new ProcessRequest("gio", ["mount", uri]), ct).ConfigureAwait(false);
        }
        catch (ProcessStartException ex)
        {
            log.LogDebug(ex, "gio is not available");
            return null;
        }

        var info = await runner.RunAsync(new ProcessRequest("gio", ["info", "-a", "standard::name", uri]), ct).ConfigureAwait(false);
        foreach (var line in info.StandardOutput.Split('\n'))
        {
            const string prefix = "local path:";
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal)) return trimmed[prefix.Length..].Trim();
        }

        var error = (mount.StandardError + "\n" + info.StandardError).Trim();
        if (error.Contains("not supported", StringComparison.OrdinalIgnoreCase))
        {
            log.LogDebug("gio has no backend for {Scheme}: {Error}", root.Scheme, error);
            return null;
        }
        log.LogWarning("gio could not mount {Root}: {Error}", root, error);
        throw new FolderUnavailableException($"Could not connect to {root}: {FirstLine(error)}");
    }

    private static string CleanBusctlError(string error)
    {
        var line = FirstLine(error);
        const string prefix = "Call failed:";
        return line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..].Trim() : line;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "unknown error" : line;
    }
}
