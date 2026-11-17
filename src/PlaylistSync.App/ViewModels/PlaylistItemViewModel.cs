using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PlaylistSync.App.Services;
using PlaylistSync.Core.Locations;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Sync;
using PlaylistSync.Core.Playlists;
using PlaylistSync.Core.Storage;

namespace PlaylistSync.App.ViewModels;

public enum PlaylistHealth { NeverSynced, Ok, Attention, Error }

/// <summary>One library entry in the sidebar and the detail header, with its track list and sync state.</summary>
public sealed partial class PlaylistItemViewModel(PlaylistEntry entry, ITrackActions actions, CoverLoader covers) : ViewModelBase
{
    private string? _coverUrl;
    private int _loadGeneration;
    private int _syncGeneration;
    private Manifest? _live;
    private int _toDownload;

    public TrackListViewModel Tracks { get; } = new(actions);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Url), nameof(Directory), nameof(DisplayDirectory), nameof(LastSyncText),
        nameof(HasCustomSettings), nameof(CustomSettingsText))]
    private PlaylistEntry _entry = entry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(SummaryText), nameof(Health), nameof(IsOk), nameof(NeedsAttention),
        nameof(HasError), nameof(NeverSynced), nameof(HealthTip))]
    private ManifestSummary? _summary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText), nameof(Health), nameof(IsOk), nameof(NeedsAttention), nameof(HasError),
        nameof(NeverSynced), nameof(HealthTip))]
    private string? _manifestError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSyncError))]
    private bool _isSyncing;

    /// <summary>"Fetching playlist…", "Downloading 3 / 12".</summary>
    [ObservableProperty]
    private string _syncText = "";

    [ObservableProperty]
    private double _syncPercent;

    [ObservableProperty]
    private bool _isSyncIndeterminate = true;

    /// <summary>Why the last sync could not run (user-facing), until the next sync starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSyncError))]
    private string? _syncError;

    public bool ShowSyncError => SyncError is not null && !IsSyncing;

    internal CancellationTokenSource? SyncCts { get; private set; }
    internal Task? SyncTask { get; set; }

    /// <summary>The playlist image, or null (the view shows a placeholder).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? _cover;

    public bool HasCover => Cover is not null;

    public Guid Id => Entry.Id;
    public string Name => Entry.Name;
    public string Url => Entry.Url;
    public string Directory => Entry.Directory;
    public string DisplayDirectory => ShortenHome(Entry.Directory);
    public string LastSyncText => "Last sync " + RelativeTime.Format(Entry.LastSync, DateTimeOffset.Now);

    /// <summary>The playlist has its own download options instead of the global ones.</summary>
    public bool HasCustomSettings => Entry.Overrides is not null;
    public string CustomSettingsText => Entry.Overrides is { } o ? "Own settings: " + DownloadOptionsViewModel.Describe(o) : "";

    public string CountText => Summary is { InPlaylist: > 0 } s ? s.InPlaylist.ToString(System.Globalization.CultureInfo.CurrentCulture) : "—";

    public string SummaryText => ManifestError ?? (Summary is { } s && (s.InPlaylist > 0 || s.RemovedRemote > 0)
        ? s.Describe()
        : "Not synced yet");

    public PlaylistHealth Health => ManifestError is not null ? PlaylistHealth.Error
        : Summary is null || Summary.InPlaylist + Summary.RemovedRemote == 0 ? PlaylistHealth.NeverSynced
        : Summary.NeedsAttention ? PlaylistHealth.Attention
        : PlaylistHealth.Ok;

    // Separate flags so the view can bind style classes.
    public bool IsOk => Health == PlaylistHealth.Ok;
    public bool NeedsAttention => Health == PlaylistHealth.Attention;
    public bool HasError => Health == PlaylistHealth.Error;
    public bool NeverSynced => Health == PlaylistHealth.NeverSynced;

    public string HealthTip => Health switch
    {
        PlaylistHealth.Ok => "Up to date",
        PlaylistHealth.Attention => "Has removed, failed or unavailable tracks",
        PlaylistHealth.Error => "The sync state can't be read",
        _ => "Not synced yet",
    };

    /// <summary>
    /// Reads the manifest and scans the folder for untracked files, then fills the track list and the counts.
    /// Skipped while a sync is running (its events keep the list current). Never throws.
    /// </summary>
    public async Task LoadTracksAsync(ManifestStore manifests, FolderResolver folders)
    {
        if (IsSyncing) return;
        var generation = ++_loadGeneration;
        var address = Entry.Directory;
        try
        {
            var (manifest, untracked) = await Task.Run(async () =>
            {
                var dir = (await folders.ResolveAsync(address).ConfigureAwait(false)).Path;
                var m = await manifests.LoadAsync(dir).ConfigureAwait(false);
                return (m, SyncPlanner.FindUntracked(m, DiskSnapshot.Scan(dir)));
            });
            if (generation != _loadGeneration || IsSyncing) return;
            Summary = ManifestSummary.From(manifest);
            ManifestError = null;
            SetCoverUrl(manifest?.CoverUrl);
            Tracks.Reset(TrackList.Build(manifest, untracked));
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
        {
            if (generation != _loadGeneration || IsSyncing) return;
            Summary = null;
            ManifestError = ex.Message;
            Tracks.Reset([]);
        }
    }

    /// <summary>Marks the start of a sync. Returns the token for its events (see <see cref="Apply"/>).</summary>
    internal int BeginSync(CancellationTokenSource cts)
    {
        SyncCts = cts;
        _loadGeneration++; // a load still in flight must not overwrite the live list
        _live = null;
        _toDownload = 0;
        SyncError = null;
        SyncPercent = 0;
        IsSyncIndeterminate = true;
        SyncText = "Starting…";
        IsSyncing = true;
        return ++_syncGeneration;
    }

    /// <summary>Applies a sync event (on the UI thread). Events of an older or finished sync are ignored.</summary>
    internal void Apply(int generation, SyncEvent e)
    {
        if (generation != _syncGeneration || !IsSyncing) return;
        switch (e)
        {
            case SyncStageChanged { Stage: var stage }:
                SyncText = stage switch
                {
                    SyncStage.ReadingCookies => "Reading browser cookies…",
                    SyncStage.Fetching => "Fetching playlist…",
                    SyncStage.Planning => "Checking folder…",
                    SyncStage.Downloading => $"Downloading 0 / {_toDownload}",
                    _ => "Finishing…",
                };
                IsSyncIndeterminate = stage != SyncStage.Downloading;
                break;
            case SyncPlanned planned:
                _live = planned.Manifest;
                _toDownload = planned.ToDownload.Count;
                SetCoverUrl(_live.CoverUrl);
                Summary = ManifestSummary.From(_live);
                ManifestError = null;
                Tracks.Reset(TrackList.Build(_live, planned.Untracked));
                if (_toDownload == 0) SyncText = "Nothing to download";
                break;
            case TrackUpdated updated:
                if (_live is not null)
                {
                    _live.Tracks[updated.Id] = updated.Record;
                    Summary = ManifestSummary.From(_live);
                }
                Tracks.Update(updated.Id, updated.Record);
                if (updated.Total > 0)
                {
                    SyncPercent = updated.Completed * 100.0 / updated.Total;
                    SyncText = updated.Completed == updated.Total ? "Finishing…" : $"Downloading {updated.Completed} / {updated.Total}";
                }
                break;
            case TrackProgress progress:
                Tracks.SetProgress(progress.Id, progress.Progress);
                break;
        }
    }

    /// <summary>Shows the final state of a finished sync. Later events of that sync are ignored.</summary>
    internal void Complete(SyncResult result)
    {
        _syncGeneration++;
        SetCoverUrl(result.Manifest.CoverUrl);
        Summary = ManifestSummary.From(result.Manifest);
        ManifestError = null;
        Tracks.Reset(TrackList.Build(result.Manifest, result.Untracked));
    }

    internal void EndSync(string? error)
    {
        _syncGeneration++;
        _live = null;
        SyncCts?.Dispose();
        SyncCts = null;
        SyncError = error;
        IsSyncing = false;
        SyncText = "";
    }

    /// <summary>Re-reads the folder's manifest for the counts. Never throws.</summary>
    public async Task RefreshSummaryAsync(ManifestStore manifests, FolderResolver folders, CancellationToken ct = default)
    {
        var address = Entry.Directory;
        try
        {
            var manifest = await Task.Run(async () =>
            {
                var dir = (await folders.ResolveAsync(address, ct).ConfigureAwait(false)).Path;
                return await manifests.LoadAsync(dir, ct).ConfigureAwait(false);
            }, ct);
            Summary = ManifestSummary.From(manifest);
            SetCoverUrl(manifest?.CoverUrl);
            ManifestError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
        {
            Summary = null;
            ManifestError = ex.Message;
        }
    }

    /// <summary>
    /// Shows the cover for <paramref name="url"/> (fetched once, then cached). A null URL keeps the current image:
    /// a folder without sync history yet says nothing about the cover.
    /// </summary>
    private void SetCoverUrl(string? url)
    {
        if (url is null || url == _coverUrl) return;
        _coverUrl = url;
        _ = LoadCoverAsync(url);
    }

    private async Task LoadCoverAsync(string url)
    {
        var bitmap = await covers.LoadAsync(Id, url);
        if (url != _coverUrl)
        {
            bitmap?.Dispose(); // superseded by a newer cover meanwhile
            return;
        }
        if (bitmap is null) return; // keep what we show (e.g. offline)
        var old = Cover;
        Cover = bitmap;
        old?.Dispose();
    }

    /// <summary>Forgets the cover (after the folder changed).</summary>
    internal void ResetCover()
    {
        _coverUrl = null;
        var old = Cover;
        Cover = null;
        old?.Dispose();
    }

    private static string ShortenHome(string path)
    {
        if (OperatingSystem.IsWindows()) return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) return path;
        if (path == home) return "~";
        var prefix = home.EndsWith(Path.DirectorySeparatorChar) ? home : home + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal) ? "~" + Path.DirectorySeparatorChar + path[prefix.Length..] : path;
    }
}
