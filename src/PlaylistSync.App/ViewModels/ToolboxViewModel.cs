using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlaylistSync.App.Services;
using PlaylistSync.Core;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Locations;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playlists;

namespace PlaylistSync.App.ViewModels;

/// <summary>
/// The toolbox: a one-off download of a single URL into any folder, plus a few shortcuts. A singleton, so a download
/// keeps running (and its progress is still there) when the window is closed and opened again.
/// </summary>
public sealed partial class ToolboxViewModel(
    SingleDownloader downloader,
    SettingsService settings,
    IDialogService dialogs,
    AppPaths paths,
    ILogger<ToolboxViewModel> log) : ViewModelBase
{
    public static IReadOnlyList<Choice<AudioFormat>> AudioFormats => DownloadOptionsViewModel.Formats;

    public static readonly IReadOnlyList<Choice<int?>> VideoQualities =
    [
        new(null, "Best available"),
        new(2160, "Up to 2160p (4K)"),
        new(1440, "Up to 1440p"),
        new(1080, "Up to 1080p"),
        new(720, "Up to 720p"),
        new(480, "Up to 480p"),
        new(360, "Up to 360p"),
    ];

    private CancellationTokenSource? _cts;
    private Task? _running;
    private bool _loaded;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private string _url = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsVideo))]
    private bool _isAudio = true;

    [ObservableProperty]
    private Choice<AudioFormat> _selectedAudioFormat = DownloadOptionsViewModel.Formats[0];

    [ObservableProperty]
    private Choice<int?> _selectedVideoQuality = VideoQualities[3];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private string _folder = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _errorText = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResult), nameof(ResultName))]
    [NotifyCanExecuteChangedFor(nameof(OpenResultFolderCommand), nameof(PlayResultCommand))]
    private string? _resultFile;

    [ObservableProperty] private string _utilityStatus = "";

    public bool IsVideo
    {
        get => !IsAudio;
        set => IsAudio = !value;
    }

    public bool HasResult => ResultFile is not null;
    public string ResultName => ResultFile is null ? "" : Path.GetFileName(ResultFile);

    /// <summary>Restores the last used choices. Called each time the window opens; only the first call does anything.</summary>
    public void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var t = settings.Current.Toolbox;
        IsAudio = !t.Video;
        SelectedAudioFormat = AudioFormats.FirstOrDefault(f => f.Value == t.AudioFormat) ?? AudioFormats[0];
        SelectedVideoQuality = VideoQualities.FirstOrDefault(q => q.Value == t.MaxHeight) ?? VideoQualities[0];
        Folder = string.IsNullOrWhiteSpace(t.Folder) ? DefaultFolder() : t.Folder;
    }

    private static string DefaultFolder()
    {
        var downloads = Path.Combine(PathUtil.Home, "Downloads");
        return Directory.Exists(downloads) ? downloads : PathUtil.MusicDir;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var start = FolderResolver.IsRemote(Folder) ? null : Folder;
        while (!string.IsNullOrEmpty(start) && !Directory.Exists(start))
            start = Path.GetDirectoryName(start);
        var picked = await dialogs.PickFolderAsync("Save downloads to", string.IsNullOrEmpty(start) ? null : start);
        if (picked is not null) Folder = picked;
    }

    private bool CanDownload => !IsBusy && !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Folder);

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        var url = Url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            ErrorText = "Enter a web address starting with http:// or https://.";
            return;
        }

        var folder = PathUtil.NormalizeUserPath(Folder, PathUtil.Home);
        if (folder is null)
        {
            ErrorText = "Choose a full folder path.";
            return;
        }

        ErrorText = "";
        ResultFile = null;
        IsBusy = true;
        IsIndeterminate = true;
        Percent = 0;
        ProgressText = "Starting…";
        _cts = new CancellationTokenSource();

        var current = settings.Current;
        var remembered = current.Toolbox with
        {
            Folder = folder,
            Video = IsVideo,
            AudioFormat = SelectedAudioFormat.Value,
            MaxHeight = SelectedVideoQuality.Value,
        };
        if (remembered != current.Toolbox)
        {
            try
            {
                current = current with { Toolbox = remembered };
                await settings.SaveAsync(current);
            }
            catch (Exception ex)
            {
                // Remembering the choices is a convenience; the download goes ahead anyway.
                log.LogWarning(ex, "Could not save the toolbox settings");
            }
        }

        try
        {
            var progress = new Progress<DownloadProgress>(SetProgress);
            var token = _cts.Token;
            var task = Task.Run(() => downloader.DownloadAsync(url, folder,
                IsVideo ? SingleDownloadKind.Video : SingleDownloadKind.Audio,
                remembered.AudioFormat, remembered.MaxHeight, current, progress, token));
            _running = task;
            var result = await task;
            if (result.Succeeded)
            {
                ResultFile = result.Files[^1];
                ProgressText = result.Files.Count == 1 ? "Done" : $"Done: {result.Files.Count} files";
            }
            else
            {
                ErrorText = result.Error?.Message ?? "The download failed.";
                ProgressText = "";
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Toolbox download failed");
            ErrorText = ex.Message;
            ProgressText = "";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _running = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Cancels a running download and waits (bounded) for yt-dlp to exit. Used when the app quits.</summary>
    public async Task StopAsync()
    {
        if (_running is not { } running) return;
        _cts?.Cancel();
        try
        {
            await running.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "The toolbox download did not stop cleanly before quitting");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetProgress(DownloadProgress p)
    {
        Dispatcher.UIThread.VerifyAccess();
        switch (p.Phase)
        {
            case DownloadPhase.Starting:
                IsIndeterminate = true;
                ProgressText = "Starting…";
                break;
            case DownloadPhase.Downloading when p.Percent is { } pct:
                IsIndeterminate = false;
                Percent = pct;
                ProgressText = p.BytesPerSecond is { } bps ? $"{pct:0}% · {TrackRowViewModel.FormatSpeed(bps)}" : $"{pct:0}%";
                break;
            case DownloadPhase.Downloading:
                IsIndeterminate = true;
                ProgressText = p.BytesPerSecond is { } speed ? TrackRowViewModel.FormatSpeed(speed) : "Downloading…";
                break;
            case DownloadPhase.Processing:
                IsIndeterminate = true;
                ProgressText = (p.Step ?? "Processing") + "…";
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task OpenResultFolderAsync()
    {
        if (ResultFile is { } file && Path.GetDirectoryName(file) is { } dir && !await dialogs.OpenFolderAsync(dir))
            ErrorText = $"Could not open '{dir}'.";
    }

    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task PlayResultAsync()
    {
        if (ResultFile is { } file && !await dialogs.OpenFileAsync(file))
            ErrorText = $"Could not open '{file}'.";
    }

    // ───────────── Utilities ─────────────

    [RelayCommand]
    private Task OpenLogFolderAsync() => OpenUtilityFolderAsync(paths.LogDir);

    [RelayCommand]
    private Task OpenConfigFolderAsync() => OpenUtilityFolderAsync(paths.ConfigDir);

    [RelayCommand]
    private Task OpenDataFolderAsync() => OpenUtilityFolderAsync(paths.DataDir);

    private async Task OpenUtilityFolderAsync(string dir)
    {
        UtilityStatus = await dialogs.OpenFolderAsync(dir) ? "" : $"Could not open '{dir}'.";
    }

    /// <summary>Deletes the toolbox's leftover intermediates (from cancelled or failed downloads). Never touches saved files.</summary>
    [RelayCommand]
    private void ClearTempFiles()
    {
        if (IsBusy)
        {
            UtilityStatus = "Wait for the running download to finish first.";
            return;
        }
        var temp = Path.Combine(paths.DataDir, "toolbox-tmp");
        try
        {
            long bytes = 0;
            if (Directory.Exists(temp))
            {
                bytes = new DirectoryInfo(temp).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                Directory.Delete(temp, recursive: true);
            }
            UtilityStatus = bytes > 0 ? $"Freed {bytes / (1024.0 * 1024):0.#} MB." : "Nothing to clear.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not clear {Dir}", temp);
            UtilityStatus = $"Could not clear the temp files: {ex.Message}";
        }
    }
}
