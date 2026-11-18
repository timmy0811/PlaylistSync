using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Covers;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Locations;
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
/// <param name="covers">Needed only for <see cref="DownloadOptions.SaveFolderCover"/>; without it no cover is saved.</param>
/// <param name="folders">Resolves network folders (smb://…); without it only local folders work.</param>
/// <param name="cookies">Reads browser cookies once per sync; without it every yt-dlp process reads the browser itself.</param>
/// <param name="replacements">Finds a matching upload for a gone video (<see cref="DownloadOptions.FindReplacements"/>); without it none is searched.</param>
public sealed class SyncService(
    IEnumerable<IPlaylistSource> sources,
    ManifestStore manifests,
    ITrackDownloader downloader,
    ToolLocator tools,
    TimeProvider clock,
    ILogger<SyncService> log,
    ICoverStore? covers = null,
    FolderResolver? folders = null,
    BrowserCookieCache? cookies = null,
    IReplacementFinder? replacements = null)
{
    private readonly FolderResolver _folders = folders ?? FolderResolver.LocalOnly;

    /// <summary>Hidden folder inside the playlist folder that holds in-progress downloads.</summary>
    public const string TempDirName = ".playlist-sync-tmp";

    /// <summary>After a replacement search finds nothing, the track is searched again only after this long.</summary>
    public static readonly TimeSpan ReplacementRetryInterval = TimeSpan.FromDays(7);

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

    /// <summary>
    /// Deletes the local files of tracks that are gone remotely (see <see cref="TrackList.CanDeleteFile"/>), on the
    /// user's explicit request. A removed-in-remote track is then forgotten; an unavailable one keeps its record
    /// without a file. Untracked files and tracks still in the playlist are never deleted. Refused while the folder
    /// is being synced. Rewrites the m3u8 file when that is enabled.
    /// </summary>
    /// <exception cref="SyncException">The folder is being synced, or the manifest can't be read or saved.</exception>
    public async Task<DeleteFilesResult> DeleteFilesAsync(
        PlaylistEntry entry, AppSettings settings, IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var key = Key(entry.Directory);
        if (!_running.TryAdd(key, 0))
            throw new SyncException(SyncErrorKind.AlreadyRunning, $"'{entry.Name}' is being synced. Try again when it has finished.");
        try
        {
            var dir = (await ResolveFolderAsync(entry, ct).ConfigureAwait(false)).Path;
            var manifest = await LoadManifestAsync(dir, ct).ConfigureAwait(false)
                           ?? throw new SyncException(SyncErrorKind.Manifest, $"The folder '{dir}' has no sync history.");

            var deleted = new List<string>();
            var failed = new List<DeleteFailure>();
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                if (!manifest.Tracks.TryGetValue(id, out var record) || !TrackList.CanDeleteFile(record))
                {
                    failed.Add(new DeleteFailure(id, "Only files of tracks that are gone from the playlist can be deleted."));
                    continue;
                }
                var path = TrackList.ResolveFile(dir, record.File!);
                if (path is null)
                {
                    failed.Add(new DeleteFailure(id, $"'{record.File}' is not a file inside the playlist folder."));
                    continue;
                }
                try
                {
                    File.Delete(path); // no error if it is already gone
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.LogWarning(ex, "Could not delete {Path}", path);
                    failed.Add(new DeleteFailure(id, ex.Message));
                    continue;
                }

                log.LogInformation("Deleted '{File}' of {Id} ({Status}) on user request", record.File, id, record.Status);
                if (record.Status == TrackStatus.RemovedRemote)
                    manifest.Tracks.Remove(id);
                else
                    record.File = null;
                deleted.Add(id);
            }

            if (deleted.Count > 0)
            {
                // The files are gone: record that even if the caller gave up waiting.
                await SaveUnlockedAsync(dir, manifest).ConfigureAwait(false);
                if (entry.EffectiveOptions(settings).WriteM3u)
                {
                    try
                    {
                        await M3uWriter.WriteAsync(dir, entry.Name, manifest, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        log.LogWarning(ex, "Could not update the m3u8 file for '{Name}'", entry.Name);
                    }
                }
            }
            return new DeleteFilesResult(manifest, deleted, failed);
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

    private async Task<SyncResult> RunAsync(
        PlaylistEntry entry, AppSettings settings, IProgress<SyncEvent>? progress, bool allowDifferentPlaylist, CancellationToken ct)
    {
        var options = entry.EffectiveOptions(settings);
        log.LogInformation("Sync of '{Name}' started ({Url} → {Dir})", entry.Name, entry.Url, entry.Directory);
        var folder = await ResolveFolderAsync(entry, ct).ConfigureAwait(false);
        var dir = folder.Path;
        if (folder.IsRemote) log.LogInformation("Network folder {Address} is at {Dir}", entry.Directory, dir);

        var current = await LoadManifestAsync(dir, ct).ConfigureAwait(false);

        var browserCookies = await ReadBrowserCookiesAsync(options, settings.Tools, progress, ct).ConfigureAwait(false);

        progress?.Report(new SyncStageChanged(SyncStage.Fetching));
        RemotePlaylist remote;
        using (var fetchCookies = browserCookies?.WriteCopy())
            remote = await FetchAsync(entry, settings, fetchCookies?.Apply(options) ?? options, ct).ConfigureAwait(false);

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
        // Duplicate detection and replacements match songs (artist, title, length): music playlists only.
        var music = entry.Kind == PlaylistKind.Music;
        var plan = SyncPlanner.Plan(remote, current, disk, entry.Url, options.RedownloadMissing, clock.GetUtcNow(),
            options.SkipDuplicates && music, entry.Kind);

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
            var tempRoot = PrepareTempRoot(folder.IsRemote ? _folders.TempRootFor(entry.Directory) : Path.Combine(dir, TempDirName));
            var width = Math.Max(2, remote.Tracks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);

            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(settings.ConcurrencyFor(entry.Kind), 1, 8), CancellationToken = ct };
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

                    // Each yt-dlp process gets its own cookie file: yt-dlp writes the jar back when it exits.
                    using var trackCookies = browserCookies?.WriteCopy();
                    var request = new DownloadRequest(id, dir, tempRoot, trackCookies?.Apply(options) ?? options, downloadTools)
                    {
                        TrackNumber = record.RemoteIndex + 1,
                        TrackNumberWidth = width,
                        Kind = entry.Kind,
                    };
                    var trackProgress = progress is null ? null : new InlineProgress<DownloadProgress>(p => progress.Report(new TrackProgress(id, p)));

                    DownloadResult result;
                    ReplacementAttempt? replacement = null;
                    try
                    {
                        result = await downloader.DownloadAsync(request, trackProgress, token).ConfigureAwait(false);
                        if (result.Error?.Kind == DownloadErrorKind.Unavailable && replacements is not null && options.FindReplacements && music)
                        {
                            replacement = await TryReplacementAsync(request, snapshot, result, trackProgress, token).ConfigureAwait(false);
                            if (replacement is not null) result = replacement.Result;
                        }
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

                    if (replacement?.Result.Succeeded == true) counts.AddReplaced();
                    if (result.Succeeded)
                    {
                        await UpdateAsync(id, r =>
                        {
                            r.File = result.File;
                            r.Status = TrackStatus.Synced;
                            r.DownloadedAt = clock.GetUtcNow();
                            r.LastError = null;
                            // Null when the original downloaded fine (it is back), so the record stops pointing elsewhere.
                            r.ReplacementId = replacement?.ReplacementId;
                            r.ReplacementSearchedAt = null;
                        }, countAs: TrackStatus.Synced).ConfigureAwait(false);
                    }
                    else
                    {
                        var status = result.Error!.Kind == DownloadErrorKind.Unavailable ? TrackStatus.Unavailable : TrackStatus.Failed;
                        if (result.Error.Kind == DownloadErrorKind.NeedsCookies) cookies?.Invalidate();
                        await UpdateAsync(id, r =>
                        {
                            r.Status = status;
                            r.LastError = result.Error.Message;
                            if (replacement is not null)
                            {
                                r.ReplacementId = replacement.ReplacementId;
                                r.ReplacementSearchedAt = replacement.SearchedAt ?? r.ReplacementSearchedAt;
                            }
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

        if (options.SaveFolderCover) await SaveFolderCoverAsync(entry, dir, final.CoverUrl, ct).ConfigureAwait(false);

        var finishedAt = clock.GetUtcNow();
        log.LogInformation("Sync of '{Name}' finished: {Downloaded} downloaded ({Replaced} via replacement), {Failed} failed, {Unavailable} unavailable",
            entry.Name, counts.Downloaded, counts.Replaced, counts.Failed, counts.Unavailable);
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

    /// <param name="Result">What to record for the track: the replacement's download, or the original failure.</param>
    /// <param name="ReplacementId">The replacement to remember (null once it is gone too).</param>
    /// <param name="SearchedAt">Set when a search ran and found nothing.</param>
    private sealed record ReplacementAttempt(DownloadResult Result, string? ReplacementId, DateTimeOffset? SearchedAt);

    /// <summary>
    /// The original video is gone: download the replacement found earlier, or search for one (unless a recent search
    /// found nothing, or the manifest lacks the title, artist or length to match on). Null when nothing was tried, or
    /// the search failed (the next sync tries again).
    /// </summary>
    private async Task<ReplacementAttempt?> TryReplacementAsync(
        DownloadRequest request, TrackRecord record, DownloadResult original, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var candidateId = record.ReplacementId;
        if (candidateId is null)
        {
            if (string.IsNullOrWhiteSpace(record.Title) || string.IsNullOrWhiteSpace(record.Uploader) || record.DurationSec is not > 0)
                return null;
            if (record.ReplacementSearchedAt is { } searched && clock.GetUtcNow() - searched < ReplacementRetryInterval)
                return null;

            progress?.Report(new DownloadProgress(DownloadPhase.SearchingReplacement));
            var query = new ReplacementQuery(request.VideoId, record.Title, record.Uploader, record.DurationSec.Value);
            var found = await replacements!.FindAsync(query, request.Options, request.Tools, ct).ConfigureAwait(false);
            switch (found.Outcome)
            {
                case ReplacementOutcome.Error:
                    return null;
                case ReplacementOutcome.NotFound:
                    return new ReplacementAttempt(DownloadResult.Failure(DownloadErrorKind.Unavailable,
                        original.Error!.Message + "\nNo matching upload of this song was found on YouTube Music."), null, clock.GetUtcNow());
            }
            candidateId = found.Replacement!.VideoId;
        }

        var result = await downloader.DownloadAsync(request with { SourceVideoId = candidateId }, progress, ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            log.LogInformation("Downloaded {Id} from its replacement {Replacement}", request.VideoId, candidateId);
            return new ReplacementAttempt(result, candidateId, null);
        }

        // A gone replacement is forgotten so the next sync searches afresh; any other failure is retried as is.
        var gone = result.Error!.Kind == DownloadErrorKind.Unavailable;
        return new ReplacementAttempt(
            new DownloadResult(null, result.Error with { Message = $"{original.Error!.Message}\nReplacement {candidateId}: {result.Error.Message}" }),
            gone ? null : candidateId,
            null);
    }

    /// <summary>Best effort: a missing cover never fails the sync.</summary>
    private async Task SaveFolderCoverAsync(PlaylistEntry entry, string dir, string? url, CancellationToken ct)
    {
        if (covers is null || url is null) return;
        if (PlaylistCover.IsVideoThumbnail(url))
        {
            log.LogDebug("'{Name}' has no cover of its own (only a video thumbnail); no cover file saved", entry.Name);
            return;
        }
        try
        {
            if (FolderCover.FindExisting(dir) is not null) return;
            var image = await covers.GetAsync(entry.Id, url, ct).ConfigureAwait(false);
            if (image is null) return;
            var result = await FolderCover.SaveAsync(dir, image, ct).ConfigureAwait(false);
            if (result == FolderCoverResult.Written)
                log.LogInformation("Saved the cover of '{Name}' into its folder", entry.Name);
            else if (result == FolderCoverResult.UnsupportedFormat)
                log.LogInformation("The cover of '{Name}' is not JPEG or PNG; no cover file saved", entry.Name);
        }
        catch (OperationCanceledException)
        {
            // The tracks are done; skipping the cover is fine.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not save the cover file for '{Name}'", entry.Name);
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
            // The user will likely sign in again in the browser; don't keep serving the old cookies.
            if (ex.Kind == FetchErrorKind.NeedsCookies) cookies?.Invalidate();
            throw new SyncException(ex.Kind == FetchErrorKind.ToolMissing ? SyncErrorKind.ToolMissing : SyncErrorKind.Fetch,
                ex.Message, ex);
        }
    }

    /// <summary>
    /// Reads the browser's cookies once for the whole sync. Null when cookies don't come from a browser, there is no
    /// cache, yt-dlp is missing (the fetch reports that) or the read failed (yt-dlp then reads the browser per process).
    /// </summary>
    private async Task<CookieSnapshot?> ReadBrowserCookiesAsync(
        DownloadOptions options, ToolSettings toolSettings, IProgress<SyncEvent>? progress, CancellationToken ct)
    {
        if (cookies is null || options.CookieMode != CookieMode.Browser) return null;
        if (tools.Locate(ToolKind.YtDlp, toolSettings) is not { Exists: true } ytDlp) return null;
        progress?.Report(new SyncStageChanged(SyncStage.ReadingCookies));
        return await cookies.GetAsync(options, ytDlp, ct).ConfigureAwait(false);
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

    private async Task<ResolvedFolder> ResolveFolderAsync(PlaylistEntry entry, CancellationToken ct)
    {
        try
        {
            return await _folders.ResolveAsync(entry.Directory, ct).ConfigureAwait(false);
        }
        catch (FolderUnavailableException ex)
        {
            throw new SyncException(SyncErrorKind.Directory, ex.Message, ex);
        }
    }

    private static string PrepareTempRoot(string tempRoot)
    {
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
        private int _downloaded, _failed, _unavailable, _replaced;

        public int Total { get; } = total;
        public int Downloaded => Volatile.Read(ref _downloaded);
        public int Failed => Volatile.Read(ref _failed);
        public int Unavailable => Volatile.Read(ref _unavailable);
        public int Replaced => Volatile.Read(ref _replaced);
        public int Completed => Downloaded + Failed + Unavailable;

        public void AddReplaced() => Interlocked.Increment(ref _replaced);

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
