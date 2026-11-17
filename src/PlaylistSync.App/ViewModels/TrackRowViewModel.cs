using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Sync;

namespace PlaylistSync.App.ViewModels;

/// <summary>Row actions, implemented by the main window view model (context menus live in popups, so rows carry their commands).</summary>
public interface ITrackActions
{
    Task DeleteFileAsync(TrackRowViewModel row);
    Task PlayAsync(TrackRowViewModel row);
    Task OpenFolderAsync(TrackRowViewModel row);
    Task CopyUrlAsync(TrackRowViewModel row);
    /// <summary>The track's small image (made now if a downloaded track has none yet), or null.</summary>
    Task<Bitmap?> LoadThumbnailAsync(TrackRowViewModel row);
}

/// <summary>One row of the track list: a manifest track or an untracked audio file.</summary>
public sealed partial class TrackRowViewModel : ViewModelBase
{
    private readonly ITrackActions _actions;

    public TrackRowViewModel(TrackListItem item, ITrackActions actions)
    {
        _actions = actions;
        Key = item.Key;
        Id = item.Id;
        UntrackedFile = item.UntrackedFile;
        if (item.Record is { } record) Update(record);
        else File = item.UntrackedFile;
    }

    public string Key { get; }
    /// <summary>Video id; null for an untracked file.</summary>
    public string? Id { get; }
    public string? UntrackedFile { get; }
    public bool IsUntracked => Id is null;
    public bool HasId => Id is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string? _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    private string? _uploader;

    [ObservableProperty]
    private string _durationText = "";

    public double? DurationSec { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle), nameof(Details), nameof(HasFile))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(DeleteFileCommand))]
    private string? _file;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIcon), nameof(StatusLabel), nameof(HasStatusLabel), nameof(IsRemoved), nameof(IsUnavailable),
        nameof(IsFailed), nameof(IsMissing), nameof(IsDownloading), nameof(IsSynced), nameof(IsPending), nameof(Details), nameof(ShowError),
        nameof(CanDelete), nameof(StatusTip), nameof(IsReplaced), nameof(UrlId), nameof(IsDuplicate))]
    [NotifyCanExecuteChangedFor(nameof(DeleteFileCommand))]
    private TrackStatus? _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError), nameof(StatusTip))]
    private string? _error;

    /// <summary>The upload the file came from, when the playlist's own video is gone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReplaced), nameof(StatusLabel), nameof(HasStatusLabel), nameof(StatusTip), nameof(UrlId))]
    private string? _replacementId;

    /// <summary>With <see cref="TrackStatus.Duplicate"/>: the track that holds the same song.</summary>
    public string? DuplicateOf { get; private set; }

    /// <summary>Title of <see cref="DuplicateOf"/>, filled in by the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTip))]
    private string? _duplicateOfTitle;

    /// <summary>Downloaded from a replacement upload.</summary>
    public bool IsReplaced => ReplacementId is not null && Status == TrackStatus.Synced;

    /// <summary>The video to link to: the replacement if the file came from one.</summary>
    public string? UrlId => IsReplaced ? ReplacementId : Id;

    // Live download progress (only while Downloading).
    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    private string _progressText = "";

    private Bitmap? _thumbnail;
    private bool _thumbnailRequested;

    /// <summary>
    /// The small image left of the title. Loaded on first use, so only rows the list actually shows read a file;
    /// null (placeholder shown) for untracked files and tracks that never got one.
    /// </summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested && Id is not null)
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync();
            }
            return _thumbnail;
        }
    }

    private async Task LoadThumbnailAsync()
    {
        var bitmap = await _actions.LoadThumbnailAsync(this);
        if (bitmap is null) return;
        _thumbnail = bitmap;
        OnPropertyChanged(nameof(Thumbnail));
    }

    public string DisplayTitle => Title ?? (File is null ? Id ?? "" : Path.GetFileNameWithoutExtension(File));

    /// <summary>Second line: uploader and file name.</summary>
    public string Details
    {
        get
        {
            if (IsUntracked) return "Not downloaded by Playlist Sync · " + File;
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(Uploader)) parts.Add(Uploader!);
            if (File is not null) parts.Add(File);
            return string.Join(" · ", parts);
        }
    }

    public bool HasFile => File is not null;
    public bool IsRemoved => Status == TrackStatus.RemovedRemote;
    public bool IsUnavailable => Status == TrackStatus.Unavailable;
    public bool IsFailed => Status == TrackStatus.Failed;
    public bool IsMissing => Status == TrackStatus.MissingLocal;
    public bool IsDownloading => Status == TrackStatus.Downloading;
    public bool IsSynced => Status == TrackStatus.Synced;
    public bool IsPending => Status == TrackStatus.Pending;
    public bool IsDuplicate => Status == TrackStatus.Duplicate;
    /// <summary>The error replaces the details line for failed tracks; unavailable ones show it as a tooltip.</summary>
    public bool ShowError => Error is not null && Status == TrackStatus.Failed;

    /// <summary>Same rule as the core: only files of tracks gone remotely.</summary>
    public bool CanDelete => File is not null && Status is TrackStatus.RemovedRemote or TrackStatus.Unavailable;

    public string StatusIcon => Status switch
    {
        TrackStatus.Synced => "✓",
        TrackStatus.Pending => "○",
        TrackStatus.Downloading => "↓",
        TrackStatus.Failed => "✕",
        TrackStatus.MissingLocal => "!",
        TrackStatus.RemovedRemote => "⚠",
        TrackStatus.Unavailable => "⊘",
        TrackStatus.Duplicate => "=",
        _ => "?",
    };

    public string StatusLabel => Status switch
    {
        TrackStatus.Synced when IsReplaced => "Replacement",
        TrackStatus.Pending => "Pending",
        TrackStatus.Failed => "Failed",
        TrackStatus.MissingLocal => "File missing",
        TrackStatus.RemovedRemote => "Removed in remote",
        TrackStatus.Unavailable => "Unavailable in remote",
        TrackStatus.Duplicate => "Duplicate",
        null => "Untracked",
        _ => "",
    };

    public bool HasStatusLabel => StatusLabel.Length > 0 && !IsDownloading;

    public string StatusTip => Status switch
    {
        TrackStatus.Synced when IsReplaced =>
            $"The playlist's video is gone, so the same song was downloaded from another upload ({ReplacementId}).",
        TrackStatus.Synced => "Downloaded",
        TrackStatus.Pending => "Downloaded on the next sync",
        TrackStatus.Downloading => "Downloading",
        TrackStatus.Failed => "The last download failed; it is retried on the next sync",
        TrackStatus.MissingLocal => "The file was deleted from the folder. Turn on re-downloading missing files in Settings to get it back.",
        TrackStatus.RemovedRemote => "No longer in the remote playlist. The file is kept until you delete it.",
        TrackStatus.Unavailable => "Private or deleted on YouTube. The file (if any) is kept." + (Error is null ? "" : "\n" + Error),
        TrackStatus.Duplicate =>
            $"The same song is already in the playlist{(DuplicateOfTitle is null ? "" : $" as '{DuplicateOfTitle}'")}, so it is not downloaded again. " +
            "Turn off 'Skip duplicate tracks' in Settings to download it anyway.",
        _ => "An audio file in the folder that Playlist Sync didn't download. It is never touched.",
    };

    public void Update(TrackRecord record)
    {
        // A finished download just made the thumbnail: look again (the row may have shown the placeholder).
        if (Status == TrackStatus.Downloading && record.Status == TrackStatus.Synced && _thumbnail is null && _thumbnailRequested)
        {
            _thumbnailRequested = false;
            OnPropertyChanged(nameof(Thumbnail));
        }
        Title = record.Title;
        Uploader = record.Uploader;
        DurationSec = record.DurationSec;
        DurationText = FormatDuration(record.DurationSec);
        File = record.File;
        Error = record.LastError;
        ReplacementId = record.ReplacementId;
        DuplicateOf = record.DuplicateOf;
        Status = record.Status;
        if (record.Status != TrackStatus.Downloading) ResetProgress();
    }

    public void SetProgress(DownloadProgress progress)
    {
        switch (progress.Phase)
        {
            case DownloadPhase.Starting:
                IsProgressIndeterminate = true;
                ProgressText = "Starting…";
                break;
            case DownloadPhase.Downloading when progress.Percent is { } p:
                IsProgressIndeterminate = false;
                Percent = p;
                ProgressText = progress.BytesPerSecond is { } bps
                    ? $"{p:0}% · {FormatSpeed(bps)}"
                    : $"{p:0}%";
                break;
            case DownloadPhase.Downloading:
                IsProgressIndeterminate = true;
                ProgressText = progress.BytesPerSecond is { } speed ? FormatSpeed(speed) : "Downloading…";
                break;
            case DownloadPhase.Processing:
                IsProgressIndeterminate = true;
                ProgressText = (progress.Step ?? "Processing") + "…";
                break;
            case DownloadPhase.SearchingReplacement:
                IsProgressIndeterminate = true;
                ProgressText = "Finding replacement…";
                break;
        }
    }

    private void ResetProgress()
    {
        Percent = 0;
        IsProgressIndeterminate = true;
        ProgressText = "";
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteFileAsync() => _actions.DeleteFileAsync(this);

    [RelayCommand(CanExecute = nameof(HasFile))]
    private Task PlayAsync() => _actions.PlayAsync(this);

    [RelayCommand]
    private Task OpenFolderAsync() => _actions.OpenFolderAsync(this);

    [RelayCommand(CanExecute = nameof(HasId))]
    private Task CopyUrlAsync() => _actions.CopyUrlAsync(this);

    internal static string FormatDuration(double? seconds)
    {
        if (seconds is not { } s || s <= 0) return "";
        var t = TimeSpan.FromSeconds(Math.Round(s));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    internal static string FormatSpeed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / (1024 * 1024):0.0} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:0} KB/s",
        _ => $"{bytesPerSecond:0} B/s",
    };
}
