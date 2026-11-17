using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlaylistSync.App.Services;
using PlaylistSync.Core;
using PlaylistSync.Core.Locations;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playback;
using PlaylistSync.Core.Playlists;
using PlaylistSync.Core.Sources;
using PlaylistSync.Core.Storage;
using PlaylistSync.Core.Sync;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.App.ViewModels;

public partial class MainWindowViewModel(
    PlaylistLibrary library,
    SettingsService settings,
    ManifestStore manifests,
    SyncService sync,
    FolderResolver folders,
    CoverLoader covers,
    ThumbnailLoader thumbnails,
    IEnumerable<IPlaylistSource> sources,
    ToolChecker toolChecker,
    AppPaths paths,
    PlayerLauncher player,
    IDialogService dialogs,
    ToolboxViewModel toolbox,
    ILogger<MainWindowViewModel> log) : ViewModelBase, ITrackActions
{
    private readonly List<PlaylistItemViewModel> _syncing = [];
    private CancellationTokenSource? _syncAllCts;

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(EditPlaylistCommand), nameof(RemovePlaylistCommand), nameof(OpenFolderCommand),
        nameof(CopyUrlCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(SyncCommand), nameof(CancelSyncCommand),
        nameof(DeleteRemovedFilesCommand), nameof(PlaylistSettingsCommand), nameof(PlayPlaylistCommand),
        nameof(ShufflePlaylistCommand))]
    private PlaylistItemViewModel? _selectedPlaylist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    [ObservableProperty]
    private string _statusText = "Loading…";

    /// <summary>What is running right now, for the status bar ("Chill Mix: Downloading 3 / 12").</summary>
    [ObservableProperty]
    private string _activityText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelAllCommand))]
    private bool _isAnySyncing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncAllCommand))]
    private bool _isSyncingAll;

    public bool HasSelection => SelectedPlaylist is not null;
    public bool IsEmpty => IsLoaded && Playlists.Count == 0;

    private bool CanSync => SelectedPlaylist is { IsSyncing: false };
    private bool CanCancelSync => SelectedPlaylist is { IsSyncing: true };
    private bool CanChangeSelected => SelectedPlaylist is { IsSyncing: false };
    private bool CanSyncAll => !IsSyncingAll && Playlists.Count > 0;

    /// <summary>Loads settings and the library. Called once when the main window opens.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await settings.LoadAsync();
            await library.LoadAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Loading the library failed");
            StatusText = "Could not load the library";
            await dialogs.ShowErrorAsync("Could not load the library", ex.Message);
            return;
        }

        foreach (var entry in library.Entries)
            Playlists.Add(new PlaylistItemViewModel(entry, this, covers));
        IsLoaded = true;
        OnPropertyChanged(nameof(IsEmpty));
        SyncAllCommand.NotifyCanExecuteChanged();
        SelectedPlaylist = Playlists.FirstOrDefault();
        StatusText = "Ready";

        await Task.WhenAll(Playlists.Where(p => p != SelectedPlaylist).Select(p => p.RefreshSummaryAsync(manifests, folders)));
    }

    partial void OnSelectedPlaylistChanged(PlaylistItemViewModel? value)
    {
        // Re-read on every selection: files may have changed outside the app.
        if (value is not null) _ = value.LoadTracksAsync(manifests, folders);
    }

    // ───────────────────────────── Library ─────────────────────────────

    [RelayCommand]
    private async Task AddPlaylistAsync()
    {
        var editor = CreateEditor(original: null, async entry =>
        {
            await library.AddAsync(entry);
            var item = new PlaylistItemViewModel(entry, this, covers);
            Playlists.Add(item);
            OnPropertyChanged(nameof(IsEmpty));
            SyncAllCommand.NotifyCanExecuteChanged();
            SelectedPlaylist = item;
            log.LogInformation("Added playlist '{Name}' ({Url} → {Dir})", entry.Name, entry.Url, entry.Directory);
        });
        await dialogs.ShowPlaylistEditorAsync(editor);
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task EditPlaylistAsync()
    {
        if (SelectedPlaylist is not { IsSyncing: false } item) return;
        var editor = CreateEditor(item.Entry, async entry =>
        {
            var folderChanged = !string.Equals(item.Directory, entry.Directory, StringComparison.Ordinal);
            await library.UpdateAsync(entry);
            item.Entry = entry;
            item.SyncError = null;
            if (folderChanged) item.ResetCover();
            log.LogInformation("Updated playlist '{Name}' ({Url} → {Dir})", entry.Name, entry.Url, entry.Directory);
            _ = item.LoadTracksAsync(manifests, folders);
        });
        await dialogs.ShowPlaylistEditorAsync(editor);
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task RemovePlaylistAsync()
    {
        if (SelectedPlaylist is not { IsSyncing: false } item) return;
        var ok = await dialogs.ConfirmAsync(
            "Remove playlist",
            $"Remove '{item.Name}' from Playlist Sync?\n\nThe folder {item.DisplayDirectory} and all files in it are kept. " +
            "If you add the folder again later, its sync history is picked up again.",
            "Remove", destructive: true);
        if (!ok || item.IsSyncing) return;

        try
        {
            await library.RemoveAsync(item.Id);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Removing playlist '{Name}' failed", item.Name);
            await dialogs.ShowErrorAsync("Could not remove the playlist", ex.Message);
            return;
        }

        covers.Remove(item.Id);
        item.ResetCover();
        var index = Playlists.IndexOf(item);
        Playlists.Remove(item);
        OnPropertyChanged(nameof(IsEmpty));
        SyncAllCommand.NotifyCanExecuteChanged();
        SelectedPlaylist = Playlists.Count == 0 ? null : Playlists[Math.Min(index, Playlists.Count - 1)];
        log.LogInformation("Removed playlist '{Name}' from the library (folder kept)", item.Name);
    }

    // ───────────────────────────── Settings ─────────────────────────────

    [RelayCommand]
    private void OpenToolbox() => dialogs.ShowToolbox(toolbox);

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var vm = new SettingsViewModel(settings.Current, dialogs, toolChecker, paths, log, async updated =>
        {
            await settings.SaveAsync(updated);
            log.LogInformation("Settings saved");
        });
        if (await dialogs.ShowSettingsAsync(vm))
        {
            StatusText = IsAnySyncing ? "Settings saved. Running syncs keep their settings; the next sync uses the new ones." : "Settings saved";
        }
    }

    /// <summary>The selected playlist's own download options (or "use the global settings").</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task PlaylistSettingsAsync()
    {
        if (SelectedPlaylist is not { IsSyncing: false } item) return;
        var vm = CreatePlaylistSettings(item.Name, item.Entry.Overrides, async overrides =>
        {
            await library.UpdateAsync(item.Id, e => e with { Overrides = overrides });
            if (library.Find(item.Id) is { } updated) item.Entry = updated;
            log.LogInformation("Playlist '{Name}' now uses {Which} download settings", item.Name, overrides is null ? "the global" : "its own");
        });
        if (await dialogs.ShowSettingsAsync(vm)) StatusText = $"Download settings of '{item.Name}' saved";
    }

    internal SettingsViewModel CreatePlaylistSettings(string name, DownloadOptions? overrides, Func<DownloadOptions?, Task> save) =>
        new(name, overrides, settings.Current, dialogs, log, save);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task MoveUpAsync() => MoveAsync(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task MoveDownAsync() => MoveAsync(+1);

    private async Task MoveAsync(int delta)
    {
        if (SelectedPlaylist is not { } item) return;
        var from = Playlists.IndexOf(item);
        var to = from + delta;
        if (to < 0 || to >= Playlists.Count) return;
        try
        {
            await library.MoveAsync(item.Id, to);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Reordering failed");
            await dialogs.ShowErrorAsync("Could not save the new order", ex.Message);
            return;
        }
        Playlists.Move(from, to);
        SelectedPlaylist = item;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpenFolderAsync()
    {
        if (SelectedPlaylist is not { } item) return;
        await OpenPlaylistFolderAsync(item, subFolder: null);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyUrlAsync()
    {
        if (SelectedPlaylist is not { } item) return;
        await dialogs.CopyTextAsync(item.Url);
        StatusText = "URL copied";
    }

    // ───────────────────────────── Sync ─────────────────────────────

    [RelayCommand(CanExecute = nameof(CanSync), AllowConcurrentExecutions = true)]
    private Task SyncAsync() => SelectedPlaylist is { } item ? StartSync(item) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanCancelSync))]
    private void CancelSync()
    {
        if (SelectedPlaylist is not { IsSyncing: true } item) return;
        item.SyncCts?.Cancel();
        item.SyncText = "Cancelling…";
    }

    /// <summary>Syncs every playlist, one after another. Playlists that are already syncing are skipped.</summary>
    [RelayCommand(CanExecute = nameof(CanSyncAll))]
    private async Task SyncAllAsync()
    {
        using var cts = new CancellationTokenSource();
        _syncAllCts = cts;
        IsSyncingAll = true;
        try
        {
            foreach (var item in Playlists.ToList())
            {
                if (cts.IsCancellationRequested) break;
                if (item.IsSyncing || !Playlists.Contains(item)) continue;
                await StartSync(item);
            }
        }
        finally
        {
            _syncAllCts = null;
            IsSyncingAll = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsAnySyncing))]
    private void CancelAll()
    {
        _syncAllCts?.Cancel();
        foreach (var item in _syncing.ToList())
        {
            item.SyncCts?.Cancel();
            item.SyncText = "Cancelling…";
        }
    }

    /// <summary>True while a sync or a toolbox download runs, i.e. quitting has to stop yt-dlp first.</summary>
    public bool IsAnythingRunning => IsAnySyncing || toolbox.IsBusy;

    /// <summary>Cancels every sync (and the toolbox download) and waits (bounded) for them to stop, so no yt-dlp process outlives the app.</summary>
    public async Task StopAllSyncsAsync()
    {
        var toolboxStopped = toolbox.StopAsync();
        var tasks = _syncing.Select(i => i.SyncTask).OfType<Task>().ToList();
        CancelAll();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));
            await toolboxStopped;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Not every sync stopped cleanly before quitting");
        }
    }

    private Task StartSync(PlaylistItemViewModel item)
    {
        if (item.IsSyncing) return item.SyncTask ?? Task.CompletedTask;

        var cts = new CancellationTokenSource();
        var generation = item.BeginSync(cts);
        _syncing.Add(item);
        item.PropertyChanged += OnSyncingItemChanged;
        OnSyncStateChanged();

        var task = RunSyncAsync(item, generation, cts.Token);
        item.SyncTask = task;
        return task;
    }

    private async Task RunSyncAsync(PlaylistItemViewModel item, int generation, CancellationToken ct)
    {
        var progress = new UiProgress<SyncEvent>(e => item.Apply(generation, e));
        string? error = null;
        var reload = true;
        try
        {
            var allowDifferent = false;
            while (true)
            {
                try
                {
                    var entry = item.Entry;
                    var result = await Task.Run(() => sync.SyncAsync(entry, settings.Current, progress, allowDifferent, ct), ct);
                    item.Complete(result);
                    reload = false;
                    await RecordLastSyncAsync(item, result.FinishedAt);
                    StatusText = Describe(item.Name, result);
                    break;
                }
                catch (SyncException ex) when (ex.Kind == SyncErrorKind.DifferentPlaylist && !allowDifferent)
                {
                    var ok = await dialogs.ConfirmAsync("Folder used by another playlist",
                        ex.Message + "\n\nSync anyway? Its tracks are then shown as removed in remote. No files are deleted.",
                        "Sync anyway");
                    if (!ok)
                    {
                        StatusText = $"Sync of '{item.Name}' skipped";
                        break;
                    }
                    allowDifferent = true;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StatusText = $"Sync of '{item.Name}' cancelled. Finished tracks are kept.";
        }
        catch (SyncException ex)
        {
            log.LogWarning("Sync of '{Name}' failed: {Message}", item.Name, ex.Message);
            error = ex.Message;
            StatusText = $"Sync of '{item.Name}' failed";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Sync of '{Name}' failed unexpectedly", item.Name);
            error = "Unexpected error: " + ex.Message;
            StatusText = $"Sync of '{item.Name}' failed";
        }
        finally
        {
            item.PropertyChanged -= OnSyncingItemChanged;
            _syncing.Remove(item);
            item.EndSync(error);
            OnSyncStateChanged();
        }

        // After a cancel or error the live list may be partial; show what is on disk now.
        if (reload) await item.LoadTracksAsync(manifests, folders);
    }

    private async Task RecordLastSyncAsync(PlaylistItemViewModel item, DateTimeOffset finishedAt)
    {
        try
        {
            await library.UpdateAsync(item.Id, e => e with { LastSync = finishedAt });
            if (library.Find(item.Id) is { } updated) item.Entry = updated;
        }
        catch (KeyNotFoundException)
        {
            // Removed from the library meanwhile; nothing to record.
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not save the last sync time of '{Name}'", item.Name);
        }
    }

    private static string Describe(string name, SyncResult r)
    {
        var parts = new List<string>();
        if (r.Downloaded > 0) parts.Add($"{r.Downloaded} downloaded");
        if (r.Failed > 0) parts.Add($"{r.Failed} failed");
        if (r.BecameUnavailable > 0) parts.Add($"{r.BecameUnavailable} unavailable");
        var removed = r.Changes.Count(c => c.To == TrackStatus.RemovedRemote);
        if (removed > 0) parts.Add($"{removed} removed in remote");
        var duplicates = r.Changes.Count(c => c.To == TrackStatus.Duplicate);
        if (duplicates > 0) parts.Add(duplicates == 1 ? "1 duplicate skipped" : $"{duplicates} duplicates skipped");
        return parts.Count == 0 ? $"'{name}' is up to date" : $"Synced '{name}': {string.Join(", ", parts)}";
    }

    private void OnSyncingItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistItemViewModel.SyncText)) UpdateActivity();
    }

    private void OnSyncStateChanged()
    {
        IsAnySyncing = _syncing.Count > 0;
        SyncCommand.NotifyCanExecuteChanged();
        CancelSyncCommand.NotifyCanExecuteChanged();
        EditPlaylistCommand.NotifyCanExecuteChanged();
        RemovePlaylistCommand.NotifyCanExecuteChanged();
        DeleteRemovedFilesCommand.NotifyCanExecuteChanged();
        PlaylistSettingsCommand.NotifyCanExecuteChanged();
        UpdateActivity();
    }

    private void UpdateActivity() =>
        ActivityText = _syncing.Count switch
        {
            0 => "",
            1 => $"{_syncing[0].Name}: {_syncing[0].SyncText}",
            var n => $"Syncing {n} playlists",
        };

    // ───────────────────────────── Track actions ─────────────────────────────

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task DeleteRemovedFilesAsync()
    {
        if (SelectedPlaylist is not { IsSyncing: false } item) return;
        var rows = item.Tracks.DeletableRemoved;
        if (rows.Count == 0) return;

        const int listed = 8;
        var names = string.Join("\n", rows.Take(listed).Select(r => "• " + r.File));
        if (rows.Count > listed) names += $"\n… and {rows.Count - listed} more";
        var ok = await dialogs.ConfirmAsync(
            "Delete removed tracks",
            (rows.Count == 1
                ? "Permanently delete the file of 1 track that was removed from the remote playlist?"
                : $"Permanently delete the files of {rows.Count} tracks that were removed from the remote playlist?") +
            $"\n\n{names}\n\nThis can't be undone.",
            "Delete", destructive: true);
        if (ok) await DeleteAsync(item, rows.Select(r => r.Id!).ToList());
    }

    async Task ITrackActions.DeleteFileAsync(TrackRowViewModel row)
    {
        if (OwnerOf(row) is not { } item || row.Id is null || !row.CanDelete) return;
        if (item.IsSyncing)
        {
            StatusText = "Wait until the sync has finished before deleting files.";
            return;
        }
        var what = row.IsRemoved ? "It was removed from the remote playlist." : "It is unavailable in the remote playlist.";
        var ok = await dialogs.ConfirmAsync(
            "Delete file",
            $"Permanently delete '{row.File}'?\n\n{what} This can't be undone.",
            "Delete", destructive: true);
        if (ok) await DeleteAsync(item, [row.Id]);
    }

    private async Task DeleteAsync(PlaylistItemViewModel item, IReadOnlyCollection<string> ids)
    {
        DeleteFilesResult result;
        try
        {
            var entry = item.Entry;
            result = await Task.Run(() => sync.DeleteFilesAsync(entry, settings.Current, ids));
        }
        catch (SyncException ex)
        {
            await dialogs.ShowErrorAsync("Could not delete files", ex.Message);
            return;
        }
        finally
        {
            await item.LoadTracksAsync(manifests, folders);
        }

        StatusText = result.Deleted.Count == 1 ? "Deleted 1 file" : $"Deleted {result.Deleted.Count} files";
        if (result.Failed.Count > 0)
        {
            var details = string.Join("\n", result.Failed.Take(8).Select(f => $"• {f.Id}: {f.Message}"));
            await dialogs.ShowErrorAsync("Some files were not deleted", details);
        }
    }

    async Task<Bitmap?> ITrackActions.LoadThumbnailAsync(TrackRowViewModel row)
    {
        if (row.Id is not { } id) return null;
        if (await thumbnails.LoadAsync(id) is { } bitmap) return bitmap;
        // Downloaded before thumbnails existed: make it from the file (or YouTube's image) instead of downloading again.
        // A row being downloaded gets its thumbnail from the download.
        if (row.File is null || row.IsDownloading || OwnerOf(row) is not { } item) return null;
        return await thumbnails.CreateMissingAsync(id, item.Directory, row.File, row.ReplacementId)
            ? await thumbnails.LoadAsync(id)
            : null;
    }

    async Task ITrackActions.PlayAsync(TrackRowViewModel row)
    {
        if (OwnerOf(row) is not { } item || row.File is null) return;
        var dir = await ResolveAsync(item);
        if (dir is null) return;
        var path = TrackList.ResolveFile(dir, row.File);
        if (path is null || !await Task.Run(() => File.Exists(path)))
        {
            StatusText = $"'{row.File}' is not in the folder";
            return;
        }

        // Continue with the tracks shown below it, so a search ("culprate") or filter plays just those, in that order.
        var rows = item.Tracks.QueueFrom(row);
        if (rows.Count <= 1)
        {
            await OpenInPlayerAsync(path, $"Could not open '{row.File}'");
            return;
        }

        var queue = new List<QueueItem>(rows.Count) { new(path, row.Title, row.DurationSec) };
        foreach (var next in rows.Skip(1))
            if (TrackList.ResolveFile(dir, next.File!) is { } p) queue.Add(new QueueItem(p, next.Title, next.DurationSec));

        string file;
        try
        {
            file = await WriteQueueAsync(queue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Writing the play queue failed; playing the single track");
            await OpenInPlayerAsync(path, $"Could not open '{row.File}'");
            return;
        }

        if (await OpenInPlayerAsync(file, "Could not open the play queue. Choose a player in Settings → App."))
            StatusText = $"Playing {queue.Count} tracks from '{row.DisplayTitle}'";
    }

    // ───────────────────────────── Playback ─────────────────────────────

    /// <summary>Opens the first downloaded track in the default player, like "Play" on that row.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PlayPlaylistAsync()
    {
        if (SelectedPlaylist is not { } item) return;
        var queue = await PlayableFilesAsync(item);
        if (queue is null) return;
        var first = queue[0];
        await OpenInPlayerAsync(first.Path, $"Could not open '{Path.GetFileName(first.Path)}'");
    }

    /// <summary>
    /// Writes the downloaded tracks in random order to an M3U file and opens it with the default handler, so every
    /// player shuffles the same way. (Players have no common "turn shuffle on" switch the app could flip.)
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ShufflePlaylistAsync()
    {
        if (SelectedPlaylist is not { } item) return;
        var queue = await PlayableFilesAsync(item);
        if (queue is null) return;

        string file;
        try
        {
            file = await WriteQueueAsync(M3uPlaylist.Shuffle(queue, Random.Shared));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Writing the shuffle queue failed");
            StatusText = "Could not write the shuffled play queue";
            return;
        }

        if (await OpenInPlayerAsync(file, "Could not open the shuffled play queue. Choose a player in Settings → App."))
            StatusText = queue.Count == 1 ? "Playing 1 track shuffled" : $"Playing {queue.Count} tracks shuffled";
    }

    /// <summary>
    /// Writes a play queue to a new file in <c>&lt;data&gt;/queues</c> and returns its path. A fresh name each time, so
    /// a player that remembers state per playlist path treats every queue as new (Haruna's resume-at-last-track is
    /// handled in <see cref="M3uPlaylist.Build"/>). Queue files older than a minute are deleted first (the app's own
    /// files; a player has read its queue by then).
    /// </summary>
    private async Task<string> WriteQueueAsync(IReadOnlyList<QueueItem> queue)
    {
        var dir = Path.Combine(paths.DataDir, "queues");
        await Task.Run(() => DeleteOldQueues(dir, TimeSpan.FromMinutes(1)));
        var file = Path.Combine(dir, $"queue-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Random.Shared.Next(0x10000):x4}.m3u");
        await AtomicFile.WriteAllTextAsync(file, M3uPlaylist.Build(queue));
        return file;
    }

    private void DeleteOldQueues(string dir, TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var old in Directory.EnumerateFiles(dir, "*.m3u"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(old) < cutoff) File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.LogDebug(ex, "Could not delete the old play queue {File}", old);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Could not clean up old play queues in {Dir}", dir);
        }
    }

    /// <summary>
    /// Opens a track or play queue with the player chosen in Settings → App, or with the system default for the file
    /// type. Returns false (and shows <paramref name="failMessage"/> or the launch error) if nothing could be started.
    /// </summary>
    private async Task<bool> OpenInPlayerAsync(string file, string failMessage)
    {
        var playback = settings.Current.Playback;
        if (playback.IsSystemDefault)
        {
            if (await dialogs.OpenFileAsync(file)) return true;
            StatusText = failMessage;
            return false;
        }
        try
        {
            await Task.Run(() => player.Launch(playback, file));
            return true;
        }
        catch (PlayerLaunchException ex)
        {
            StatusText = ex.Message;
            return false;
        }
    }

    /// <summary>The playlist's downloaded files that exist on disk, in playlist order; null (with a status message) if none.</summary>
    private async Task<IReadOnlyList<QueueItem>?> PlayableFilesAsync(PlaylistItemViewModel item)
    {
        var dir = await ResolveAsync(item);
        if (dir is null) return null;
        // While searching, only the tracks shown in the list ("culprate" → shuffle just those).
        var searching = item.Tracks.IsSearching;
        var rows = item.Tracks.PlaybackQueue;
        var queue = await Task.Run(() => rows
            .Select(r => (Row: r, Path: TrackList.ResolveFile(dir, r.File!)))
            .Where(x => x.Path is not null && File.Exists(x.Path))
            .Select(x => new QueueItem(x.Path!, x.Row.Title, x.Row.DurationSec))
            .ToList());
        if (queue.Count > 0) return queue;
        StatusText = searching
            ? "None of the tracks matching the search are downloaded."
            : "No downloaded tracks to play yet. Press Sync first.";
        return null;
    }

    async Task ITrackActions.OpenFolderAsync(TrackRowViewModel row)
    {
        if (OwnerOf(row) is not { } item) return;
        // The track's own sub folder when the file name has one (e.g. an output template with folders).
        var sub = row.File is null ? null : Path.GetDirectoryName(row.File.Replace('/', Path.DirectorySeparatorChar));
        await OpenPlaylistFolderAsync(item, string.IsNullOrEmpty(sub) ? null : sub);
    }

    async Task ITrackActions.CopyUrlAsync(TrackRowViewModel row)
    {
        if (row.UrlId is null) return;
        await dialogs.CopyTextAsync(YouTubeUrl.VideoUrl(row.UrlId));
        StatusText = "Video URL copied";
    }

    private PlaylistItemViewModel? OwnerOf(TrackRowViewModel row) =>
        SelectedPlaylist is { } selected && selected.Tracks.Contains(row)
            ? selected
            : Playlists.FirstOrDefault(p => p.Tracks.Contains(row));

    /// <summary>
    /// Opens the playlist folder (or a sub folder of it) in the file manager. A network folder is opened by its
    /// address (smb://…), so the file manager shows it like any other network place instead of a mount path.
    /// </summary>
    private async Task OpenPlaylistFolderAsync(PlaylistItemViewModel item, string? subFolder)
    {
        var display = item.DisplayDirectory;
        var dir = await ResolveAsync(item);
        if (dir is null) return;
        if (subFolder is not null && TrackList.ResolveFile(dir, subFolder) is { } sub && await Task.Run(() => Directory.Exists(sub)))
        {
            dir = sub;
            display = Path.Combine(display, subFolder);
        }
        else
        {
            subFolder = null;
        }

        if (!await Task.Run(() => Directory.Exists(dir)))
        {
            StatusText = $"The folder {display} doesn't exist yet. It is created on the first sync.";
            return;
        }

        var opened = RemoteAddress.Parse(item.Directory) is { } address && !OperatingSystem.IsWindows()
            ? await dialogs.OpenUriAsync(subFolder is null
                ? address.ToUriString()
                : (address with { Segments = [.. address.Segments, .. subFolder.Split(Path.DirectorySeparatorChar)] }).ToUriString())
            : await dialogs.OpenFolderAsync(dir);
        if (!opened) StatusText = $"Could not open {display}";
    }

    /// <summary>The playlist folder as a local path, or null (with a status message) if a network folder is unreachable.</summary>
    private async Task<string?> ResolveAsync(PlaylistItemViewModel item)
    {
        try
        {
            return (await folders.ResolveAsync(item.Directory)).Path;
        }
        catch (FolderUnavailableException ex)
        {
            StatusText = ex.Message;
            return null;
        }
    }

    private PlaylistEditorViewModel CreateEditor(PlaylistEntry? original, Func<PlaylistEntry, Task> save) =>
        new(sources, manifests, folders, dialogs, log, settings.Current, library.Entries, original, save,
            (name, overrides, saveOverrides) => CreatePlaylistSettings(name, overrides, saveOverrides));

    /// <summary>Posts reports to the UI thread, in order.</summary>
    private sealed class UiProgress<T>(Action<T> apply) : IProgress<T>
    {
        public void Report(T value) => Dispatcher.UIThread.Post(() => apply(value));
    }
}
