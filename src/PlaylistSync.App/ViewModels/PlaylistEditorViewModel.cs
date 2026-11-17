using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlaylistSync.App.Services;
using PlaylistSync.Core.Locations;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playlists;
using PlaylistSync.Core.Sources;
using PlaylistSync.Core.Storage;

namespace PlaylistSync.App.ViewModels;

/// <summary>
/// Add / edit dialog. The URL is checked with a quick yt-dlp fetch (debounced) that shows the title and
/// track count and prefills the name and folder. The folder is inspected so the dialog can say what a sync will do.
/// </summary>
public sealed partial class PlaylistEditorViewModel : ViewModelBase
{
    private static readonly TimeSpan UrlDebounce = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan FolderDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IEnumerable<IPlaylistSource> _sources;
    private readonly ManifestStore _manifests;
    private readonly FolderResolver _folders;
    private readonly IDialogService _dialogs;
    private readonly ILogger _log;
    private readonly SourceContext _sourceContext;
    private readonly IReadOnlyList<PlaylistEntry> _others;
    private readonly PlaylistEntry? _original;
    private readonly Func<PlaylistEntry, Task> _save;
    private readonly Func<string, DownloadOptions?, Func<DownloadOptions?, Task>, SettingsViewModel>? _createSettings;
    private readonly AppSettings _settings;
    private readonly string _home;
    /// <summary>Where new playlist folders are suggested: the default folder from the settings, or Music.</summary>
    private readonly string _baseDir;

    private CancellationTokenSource? _urlCheck;
    private CancellationTokenSource? _folderCheck;
    private string? _autoName;
    private string? _autoDirectory;
    private string? _fetchedPlaylistId;
    private bool _submitted;

    public PlaylistEditorViewModel(
        IEnumerable<IPlaylistSource> sources,
        ManifestStore manifests,
        FolderResolver folders,
        IDialogService dialogs,
        ILogger logger,
        AppSettings settings,
        IReadOnlyList<PlaylistEntry> library,
        PlaylistEntry? original,
        Func<PlaylistEntry, Task> save,
        Func<string, DownloadOptions?, Func<DownloadOptions?, Task>, SettingsViewModel>? createSettings = null)
    {
        _createSettings = createSettings;
        _settings = settings;
        _overrides = original?.Overrides;
        _sources = sources;
        _manifests = manifests;
        _folders = folders;
        _dialogs = dialogs;
        _log = logger;
        _sourceContext = new SourceContext(settings.Tools, settings.Download);
        _others = library;
        _original = original;
        _save = save;
        _home = PathUtil.Home;
        _baseDir = PathUtil.BaseDirectory(settings.DefaultDirectory, _home, PathUtil.MusicDir);

        if (original is not null)
        {
            _name = original.Name;
            _url = original.Url;
            _directory = original.Directory;
            StartUrlCheck(immediately: true);
            StartFolderCheck(immediately: true);
        }
    }

    public bool IsEditing => _original is not null;
    public string Title => IsEditing ? "Edit playlist" : "Add playlist";
    public string SaveText => IsEditing ? "Save" : "Add";

    [ObservableProperty] private string _name = "";

    /// <summary>The playlist's own download options; null = the global ones. Saved together with the entry.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SettingsText))] private DownloadOptions? _overrides;

    public string SettingsText => Overrides is { } o
        ? "Own settings: " + DownloadOptionsViewModel.Describe(o)
        : "Global settings (" + DownloadOptionsViewModel.Describe(_settings.Download) + ")";

    public bool CanEditSettings => _createSettings is not null;
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _directory = "";

    // Online URL check
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string? _checkText;
    [ObservableProperty] private bool _checkFailed;
    /// <summary>The check proved the URL is no playlist; saving is blocked.</summary>
    private bool _urlRejected;

    // Folder inspection
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowFolderText))] private string? _folderText;
    [ObservableProperty] private bool _folderWarning;
    [ObservableProperty] private bool _folderError;

    [ObservableProperty] private string? _nameError;
    [ObservableProperty] private string? _urlError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowFolderText))] private string? _directoryError;

    /// <summary>The folder note is hidden while the path itself is invalid.</summary>
    public bool ShowFolderText => !string.IsNullOrEmpty(FolderText) && DirectoryError is null;
    [ObservableProperty] private string? _saveError;
    [ObservableProperty] private bool _isSaving;

    /// <summary>Raised with the dialog result.</summary>
    public event Action<bool>? CloseRequested;

    partial void OnNameChanged(string value) => Validate();

    partial void OnUrlChanged(string value)
    {
        _fetchedPlaylistId = null;
        _urlRejected = false;
        Validate();
        StartUrlCheck(immediately: false);
        StartFolderCheck(immediately: false);
    }

    partial void OnDirectoryChanged(string value)
    {
        Validate();
        StartFolderCheck(immediately: false);
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var start = PathUtil.NormalizeUserPath(Directory, _home);
        if (FolderResolver.IsRemote(start)) start = null; // the picker needs a local start folder
        while (start is not null && !System.IO.Directory.Exists(start))
            start = Path.GetDirectoryName(start);
        var picked = await _dialogs.PickFolderAsync("Choose the folder for this playlist", start ?? _baseDir);
        if (picked is not null) Directory = picked;
    }

    [RelayCommand]
    private async Task EditSettingsAsync()
    {
        if (_createSettings is null) return;
        var title = string.IsNullOrWhiteSpace(Name) ? "new playlist" : Name.Trim();
        // Kept in the dialog until it is saved; Cancel here discards it again.
        var vm = _createSettings(title, Overrides, o =>
        {
            Overrides = o;
            return Task.CompletedTask;
        });
        await _dialogs.ShowSettingsAsync(vm);
    }

    [RelayCommand]
    private void Cancel()
    {
        _urlCheck?.Cancel();
        _folderCheck?.Cancel();
        CloseRequested?.Invoke(false);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        _submitted = true;
        var v = Validate();
        if (!v.IsValid || _urlRejected || FolderError || IsSaving) return;

        var entry = (_original ?? new PlaylistEntry { Name = "", Url = "", Directory = "" }) with
        {
            Name = Name.Trim(),
            Url = Url.Trim(),
            Directory = v.NormalizedDirectory!,
            Overrides = Overrides,
        };

        IsSaving = true;
        SaveError = null;
        try
        {
            await _save(entry);
            _urlCheck?.Cancel();
            _folderCheck?.Cancel();
            CloseRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Saving playlist '{Name}' failed", entry.Name);
            SaveError = $"Could not save the library: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private PlaylistValidation Validate()
    {
        var v = PlaylistValidator.Validate(Name, Url, Directory, _others, _original?.Id, _home);
        // Don't nag about empty fields before the user got to them.
        NameError = Show(v.NameError, Name);
        UrlError = _urlRejected ? UrlError : Show(v.UrlError, Url);
        DirectoryError = Show(v.DirectoryError, Directory);
        return v;

        string? Show(string? error, string field) => _submitted || !string.IsNullOrWhiteSpace(field) ? error : null;
    }

    private async void StartUrlCheck(bool immediately)
    {
        _urlCheck?.Cancel();
        _urlCheck = null;
        IsChecking = false;
        CheckText = null;
        CheckFailed = false;

        var url = Url.Trim();
        var source = _sources.FirstOrDefault(s => s.CanHandle(url));
        if (source is null) return;

        var cts = new CancellationTokenSource();
        _urlCheck = cts;
        try
        {
            if (!immediately) await Task.Delay(UrlDebounce, cts.Token);
            IsChecking = true;
            CheckText = "Checking playlist…";
            var playlist = await Task.Run(() => source.FetchAsync(url, _sourceContext, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            _fetchedPlaylistId = playlist.Id;
            var unavailable = playlist.Tracks.Count(t => !t.IsAvailable);
            CheckText = $"✓ {playlist.Title ?? "Untitled playlist"} · {Tracks(playlist.Tracks.Count)}" +
                        (unavailable > 0 ? $" ({unavailable} unavailable)" : "");
            ApplyFetchedTitle(playlist.Title);
            StartFolderCheck(immediately: true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (PlaylistFetchException ex)
        {
            if (cts.IsCancellationRequested) return;
            CheckFailed = true;
            if (ex.Kind == FetchErrorKind.NotAPlaylist)
            {
                _urlRejected = true;
                UrlError = ex.Message;
                CheckText = null;
            }
            else
            {
                CheckText = "Couldn't check the playlist (you can still save it): " + ex.Message;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Checking {Url} failed", url);
            if (cts.IsCancellationRequested) return;
            CheckFailed = true;
            CheckText = "Couldn't check the playlist: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_urlCheck, cts)) IsChecking = false;
        }
    }

    /// <summary>Fills name and folder from the remote title, unless the user typed their own.</summary>
    private void ApplyFetchedTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        if (string.IsNullOrWhiteSpace(Name) || Name == _autoName)
        {
            _autoName = title.Trim();
            Name = _autoName;
        }
        if (!IsEditing && (string.IsNullOrWhiteSpace(Directory) || Directory == _autoDirectory))
        {
            _autoDirectory = UniqueFolder(Path.Combine(_baseDir, PathUtil.SafeFolderName(title)));
            Directory = _autoDirectory;
        }
    }

    /// <summary>Appends " (2)", " (3)"… while the suggestion clashes with another playlist.</summary>
    private string UniqueFolder(string path)
    {
        var candidate = path;
        for (var i = 2; i < 100; i++)
        {
            if (PlaylistValidator.Validate("x", "https://www.youtube.com/", candidate, _others, _original?.Id, _home).DirectoryError is null)
                return candidate;
            candidate = $"{path} ({i.ToString(CultureInfo.InvariantCulture)})";
        }
        return path;
    }

    private async void StartFolderCheck(bool immediately)
    {
        _folderCheck?.Cancel();
        _folderCheck = null;
        FolderText = null;
        FolderWarning = false;
        FolderError = false;

        var dir = PathUtil.NormalizeUserPath(Directory, _home);
        if (dir is null) return;

        var cts = new CancellationTokenSource();
        _folderCheck = cts;
        try
        {
            if (!immediately) await Task.Delay(FolderDebounce, cts.Token);
            var playlistId = _fetchedPlaylistId ?? YouTubeUrl.PlaylistId(Url);
            var remote = FolderResolver.IsRemote(dir);
            if (remote) FolderText = "Connecting to the network folder…";
            FolderInfo info;
            try
            {
                info = await Task.Run(async () =>
                {
                    var local = (await _folders.ResolveAsync(dir, cts.Token).ConfigureAwait(false)).Path;
                    return await FolderInspector.InspectAsync(local, playlistId, _manifests, cts.Token).ConfigureAwait(false);
                }, cts.Token);
            }
            catch (FolderUnavailableException ex)
            {
                // Not an error that blocks saving: the server may just be off right now.
                if (cts.IsCancellationRequested) return;
                FolderText = ex.Message + " You can still save; the folder is checked again when syncing.";
                FolderWarning = true;
                return;
            }
            if (cts.IsCancellationRequested) return;

            var text = info.Message;
            if (remote) text = "Network folder. " + text;
            if (IsEditing && !string.Equals(dir, _original!.Directory, StringComparison.Ordinal))
                text += " The old folder and its files stay where they are.";
            FolderText = text;
            FolderWarning = info.IsWarning;
            FolderError = info.IsError;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Inspecting folder {Dir} failed", dir);
        }
    }

    private static string Tracks(int n) => n == 1 ? "1 track" : $"{n} tracks";
}
