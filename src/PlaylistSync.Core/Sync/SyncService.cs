using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Sources;
using PlaylistSync.Core.Storage;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Sync;

/// <summary>
/// Runs one playlist sync: fetch → plan → save → download (in parallel) → m3u.
/// The manifest is saved after the plan and after every finished track, so a crash or cancel loses
/// at most the tracks in flight. Files are never deleted.
/// </summary>
public sealed class SyncService(
    IEnumerable<IPlaylistSource> sources,
    ManifestStore manifests,
    ITrackDownloader downloader,
    ToolLocator tools,
    TimeProvider clock,
    ILogger<SyncService> log)
{
    /// <summary>Hidden folder inside the playlist folder that holds in-progress downloads.</summary>
    public const string TempDirName = ".playlist-sync-tmp";

    private readonly ConcurrentDictionary<string, byte> _running = new(DiskSnapshot.PlatformComparer);

    /// <summary>True while a sync of this folder is running.</summary>
    public bool IsRunning(string directory) => _running.ContainsKey(Key(directory));

    /// <param name="allowDifferentPlaylist">
    /// Sync even if the folder's manifest belongs to another playlist (ask the user first; see
    /// <see cref="SyncErrorKind.DifferentPlaylist"/>). Its tracks then show as removed-in-remote.
    /// </param>
    /// <exception cref="SyncException">The sync could not start or the manifest could not be saved.</exception>
    /// <exception cref="OperationCanceledException">Cancelled. Finished tracks are saved; in-flight ones go back to Pending.</exception>
    public async Task<SyncResult> SyncAsync(
        PlaylistEntry entry,
        AppSettings settings,
        IProgress<SyncEvent>? progress = null,
        bool allowDifferentPlaylist = false,
        CancellationToken ct = default)
    {
        var key = Key(entry.Directory);
        if (!_running.TryAdd(key, 0))
            throw new SyncException(SyncErrorKind.AlreadyRunning, $"'{entry.Name}' is already being synced.");
        try
        {
            return await RunAsync(entry, settings, progress, allowDifferentPlaylist, ct).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

    private async Task<SyncResult> RunAsync(
        PlaylistEntry entry, AppSettings settings, IProgress<SyncEvent>? progress, bool allowDifferentPlaylist, CancellationToken ct)
    {
        var options = entry.Overrides ?? settings.Download;
        var dir = entry.Directory;
        log.LogInformation("Sync of '{Name}' started ({Url} → {Dir})", entry.Name, entry.Url, dir);

        var current = await LoadManifestAsync(dir, ct).ConfigureAwait(false);

        progress?.Report(new SyncStageChanged(SyncStage.Fetching));
        var remote = await FetchAsync(entry, settings, options, ct).ConfigureAwait(false);

        progress?.Report(new SyncStageChanged(SyncStage.Planning));
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncException(SyncErrorKind.Directory, $"Could not create the folder '{dir}': {ex.Message}", ex);
        }

        var disk = await Task.Run(() => DiskSnapshot.Scan(dir), ct).ConfigureAwait(false);
        var plan = SyncPlanner.Plan(remote, current, disk, entry.Url, options.RedownloadMissing, clock.GetUtcNow());

        if (plan.IsDifferentPlaylist && !allowDifferentPlaylist)
        {
            throw new SyncException(SyncErrorKind.DifferentPlaylist,
                $"The folder '{dir}' is already used by another playlist ('{current?.PlaylistTitle ?? current?.PlaylistId}').");
        }

        // Resolve tools before touching anything, but only demand them if there is something to download.
        var downloadTools = plan.ToDownload.Count > 0 ? ResolveTools(settings.Tools) : null;

        var manifest = plan.Manifest;
        var gate = new SemaphoreSlim(1, 1);
        await SaveAsync(dir, manifest, gate).ConfigureAwait(false);
        foreach (var change in plan.Changes)
            log.LogInformation("  {Id} '{Title}': {From} → {To}", change.Id, change.Title, change.From?.ToString() ?? "new", change.To?.ToString() ?? "dropped");
        log.LogInformation("Plan for '{Name}': {Download} to download, {Changes} changes, {Untracked} untracked files",
            entry.Name, plan.ToDownload.Count, plan.Changes.Count, plan.Untracked.Count);
        progress?.Report(new SyncPlanned(manifest.Clone(), plan.ToDownload, plan.Changes, plan.Untracked));

        var counts = new Counts(plan.ToDownload.Count);
        if (downloadTools is not null)
        {
            progress?.Report(new SyncStageChanged(SyncStage.Downloading));
            var tempRoot = PrepareTempRoot(dir);
            var width = Math.Max(2, remote.Tracks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);

            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(settings.Concurrency, 1, 8), CancellationToken = ct };
            try
            {
                await Parallel.ForEachAsync(plan.ToDownload, parallel, async (id, token) =>
                {
                    TrackRecord record;
                    TrackRecord snapshot;
                    await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        record = manifest.Tracks[id];
                        record.Status = TrackStatus.Downloading;
                        snapshot = record.Clone();
                    }
                    finally
                    {
                        gate.Release();
                    }
                    progress?.Report(new TrackUpdated(id, snapshot, counts.Completed, counts.Total));

                    var request = new DownloadRequest(id, dir, tempRoot, options, downloadTools)
                    {
                        TrackNumber = record.RemoteIndex + 1,
                        TrackNumberWidth = width,
                    };
                    var trackProgress = progress is null ? null : new InlineProgress<DownloadProgress>(p => progress.Report(new TrackProgress(id, p)));

                    DownloadResult result;
                    try
                    {
                        result = await downloader.DownloadAsync(request, trackProgress, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        await UpdateAsync(id, r => r.Status = TrackStatus.Pending, countAs: null).ConfigureAwait(false);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // A bug, not a download failure; keep the other tracks going and record it on this one.
                        log.LogError(ex, "Unexpected error while downloading {Id}", id);
                        result = DownloadResult.Failure(DownloadErrorKind.Other, ex.Message);
                    }

                    if (result.Succeeded)
                    {
                        await UpdateAsync(id, r =>
                        {
                            r.File = result.File;
                            r.Status = TrackStatus.Synced;
                            r.DownloadedAt = clock.GetUtcNow();
                            r.LastError = null;
                        }, countAs: TrackStatus.Synced).ConfigureAwait(false);
                    }
                    else
                    {
                        var status = result.Error!.Kind == DownloadErrorKind.Unavailable ? TrackStatus.Unavailable : TrackStatus.Failed;
                        await UpdateAsync(id, r =>
                        {
                            r.Status = status;
                            r.LastError = result.Error.Message;
                        }, countAs: status).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                CleanUpTempRoot(tempRoot, manifest, gate);
            }
        }

        progress?.Report(new SyncStageChanged(SyncStage.Finishing));
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        Manifest final;
        try
        {
            final = manifest.Clone();
        }
        finally
        {
            gate.Release();
        }

        if (options.WriteM3u)
        {
            try
            {
                await M3uWriter.WriteAsync(dir, entry.Name, final, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The tracks are fine; a missing playlist file is not worth failing the sync for.
                log.LogWarning(ex, "Could not write the m3u8 file for '{Name}'", entry.Name);
            }
        }

        var finishedAt = clock.GetUtcNow();
        log.LogInformation("Sync of '{Name}' finished: {Downloaded} downloaded, {Failed} failed, {Unavailable} unavailable",
            entry.Name, counts.Downloaded, counts.Failed, counts.Unavailable);
        return new SyncResult(final, plan.Changes, plan.Untracked, counts.Downloaded, counts.Failed, counts.Unavailable, finishedAt);

        async Task UpdateAsync(string id, Action<TrackRecord> change, TrackStatus? countAs)
        {
            TrackRecord snapshot;
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var record = manifest.Tracks[id];
                change(record);
                snapshot = record.Clone();
                if (countAs is { } status) counts.Add(status);
                // Clone and save inside the gate, so saves happen in order and never see a half-applied change.
                await SaveUnlockedAsync(dir, manifest).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
            progress?.Report(new TrackUpdated(id, snapshot, counts.Completed, counts.Total));
        }
    }

    private async Task<Manifest?> LoadManifestAsync(string dir, CancellationToken ct)
    {
        try
        {
            return await manifests.LoadAsync(dir, ct).ConfigureAwait(false);
        }
        catch (StorageException ex)
        {
            throw new SyncException(SyncErrorKind.Manifest, ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncException(SyncErrorKind.Manifest, $"Could not read the manifest in '{dir}': {ex.Message}", ex);
        }
    }

    private async Task<RemotePlaylist> FetchAsync(PlaylistEntry entry, AppSettings settings, DownloadOptions options, CancellationToken ct)
    {
        var source = sources.FirstOrDefault(s => s.CanHandle(entry.Url))
                     ?? throw new SyncException(SyncErrorKind.Fetch, "Only YouTube and YouTube Music playlist URLs are supported.");
        try
        {
            return await source.FetchAsync(entry.Url, new SourceContext(settings.Tools, options), ct).ConfigureAwait(false);
        }
        catch (PlaylistFetchException ex)
        {
            throw new SyncException(ex.Kind == FetchErrorKind.ToolMissing ? SyncErrorKind.ToolMissing : SyncErrorKind.Fetch,
                ex.Message, ex);
        }
    }

    private DownloadTools ResolveTools(ToolSettings settings)
    {
        var ytDlp = tools.Locate(ToolKind.YtDlp, settings);
        if (ytDlp is not { Exists: true })
        {
            throw new SyncException(SyncErrorKind.ToolMissing, ytDlp is null
                ? "yt-dlp was not found. Install it or let the app download it in Settings → Tools."
                : $"The configured yt-dlp path '{ytDlp.Path}' does not exist.");
        }
        var ffmpeg = tools.Locate(ToolKind.Ffmpeg, settings);
        if (ffmpeg is not { Exists: true })
        {
            throw new SyncException(SyncErrorKind.ToolMissing, ffmpeg is null
                ? "ffmpeg was not found. It is needed to convert downloads to audio files. Install it or set its path in Settings → Tools."
                : $"The configured ffmpeg path '{ffmpeg.Path}' does not exist.");
        }
        return new DownloadTools(ytDlp, ffmpeg, tools.Locate(ToolKind.JsRuntime, settings));
    }

    private async Task SaveAsync(string dir, Manifest manifest, SemaphoreSlim gate)
    {
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await SaveUnlockedAsync(dir, manifest).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SaveUnlockedAsync(string dir, Manifest manifest)
    {
        try
        {
            // Not cancellable: a finished track must be recorded even while the sync is being cancelled.
            await manifests.SaveAsync(dir, manifest, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncException(SyncErrorKind.Manifest, $"Could not save the manifest in '{dir}': {ex.Message}", ex);
        }
    }

    private static string PrepareTempRoot(string dir)
    {
        var tempRoot = Path.Combine(dir, TempDirName);
        var info = Directory.CreateDirectory(tempRoot);
        // The dot hides it on Linux; Windows needs the attribute so the folder scan skips it.
        if (OperatingSystem.IsWindows()) info.Attributes |= FileAttributes.Hidden;
        return tempRoot;
    }

    /// <summary>
    /// Keeps the temp dirs of tracks that still need downloading (so yt-dlp can resume them) and removes the
    /// rest; removes the temp root once it is empty. Only touches our own temp folder.
    /// </summary>
    private void CleanUpTempRoot(string tempRoot, Manifest manifest, SemaphoreSlim gate)
    {
        try
        {
            HashSet<string> keep;
            gate.Wait();
            try
            {
                keep = manifest.Tracks
                    .Where(kv => kv.Value.Status is TrackStatus.Pending or TrackStatus.Failed or TrackStatus.Downloading)
                    .Select(kv => kv.Key)
                    .ToHashSet(StringComparer.Ordinal);
            }
            finally
            {
                gate.Release();
            }

            foreach (var sub in Directory.EnumerateDirectories(tempRoot))
            {
                if (!keep.Contains(Path.GetFileName(sub))) Directory.Delete(sub, recursive: true);
            }
            if (!Directory.EnumerateFileSystemEntries(tempRoot).Any()) Directory.Delete(tempRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Could not clean up {TempRoot}", tempRoot);
        }
    }

    private static string Key(string directory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private sealed class Counts(int total)
    {
        private int _downloaded, _failed, _unavailable;

        public int Total { get; } = total;
        public int Downloaded => Volatile.Read(ref _downloaded);
        public int Failed => Volatile.Read(ref _failed);
        public int Unavailable => Volatile.Read(ref _unavailable);
        public int Completed => Downloaded + Failed + Unavailable;

        public void Add(TrackStatus status)
        {
            switch (status)
            {
                case TrackStatus.Synced: Interlocked.Increment(ref _downloaded); break;
                case TrackStatus.Failed: Interlocked.Increment(ref _failed); break;
                case TrackStatus.Unavailable: Interlocked.Increment(ref _unavailable); break;
            }
        }
    }

    /// <summary>Unlike <see cref="Progress{T}"/>, reports synchronously on the calling thread (no context capture).</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
