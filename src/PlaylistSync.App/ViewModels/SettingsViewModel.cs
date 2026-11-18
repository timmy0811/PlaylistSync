using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlaylistSync.App.Services;
using PlaylistSync.Core;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playback;
using PlaylistSync.Core.Playlists;
using PlaylistSync.Core.Settings;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.App.ViewModels;

/// <summary>
/// The settings dialog. Global scope: every setting from PLAN §6. Playlist scope: one playlist's own download options
/// (audio, metadata, files, extras), or "use the global settings".
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    public static readonly IReadOnlyList<Choice<ThemeMode>> Themes =
    [
        new(ThemeMode.System, "Follow the system"),
        new(ThemeMode.Light, "Light"),
        new(ThemeMode.Dark, "Dark"),
    ];

    public static readonly IReadOnlyList<Choice<LogLevel>> LogLevels =
    [
        new(LogLevel.Debug, "Debug (everything, incl. yt-dlp warnings)"),
        new(LogLevel.Information, "Information"),
        new(LogLevel.Warning, "Warnings and errors"),
        new(LogLevel.Error, "Errors only"),
    ];

    /// <summary>Which tab each validated field is on, for the "fix these first" note.</summary>
    private static readonly Dictionary<string, string> FieldTabs = new(StringComparer.Ordinal)
    {
        [nameof(DownloadOptions.VbrLevel)] = "Audio",
        [nameof(DownloadOptions.CbrKbps)] = "Audio",
        [nameof(DownloadOptions.FilenameTemplate)] = "Files",
        [nameof(DownloadOptions.VideoFilenameTemplate)] = "Video",
        [nameof(DownloadOptions.VideoMaxHeight)] = "Video",
        [nameof(DownloadOptions.SubtitleLanguages)] = "Video",
        [nameof(DownloadOptions.CookiesFile)] = "Network",
        [nameof(DownloadOptions.CookiesBrowser)] = "Network",
        [nameof(DownloadOptions.Proxy)] = "Network",
        [nameof(DownloadOptions.RateLimit)] = "Network",
        [nameof(DownloadOptions.SleepIntervalSeconds)] = "Network",
        [nameof(DownloadOptions.Retries)] = "Network",
        [nameof(DownloadOptions.ExtraArgs)] = "Advanced",
        [nameof(AppSettings.Concurrency)] = "Advanced",
        [nameof(AppSettings.VideoConcurrency)] = "Advanced",
        [nameof(AppSettings.DefaultDirectory)] = "App",
        [nameof(AppSettings.DefaultVideoDirectory)] = "App",
        [nameof(PlaybackSettings.ExecutablePath)] = "App",
        [nameof(ToolSettings.YtDlpPath)] = "Tools",
        [nameof(ToolSettings.FfmpegPath)] = "Tools",
        [nameof(ToolSettings.JsRuntimePath)] = "Tools",
    };

    private readonly AppSettings _original;
    private readonly IDialogService _dialogs;
    private readonly ToolChecker? _tools;
    private readonly AppPaths? _paths;
    private readonly ILogger _log;
    private readonly Func<AppSettings, Task>? _saveGlobal;
    private readonly Func<DownloadOptions?, Task>? _savePlaylist;
    private readonly DownloadOptions? _playlistOverrides;
    private CancellationTokenSource? _toolCheck;

    /// <summary>Global settings.</summary>
    public SettingsViewModel(AppSettings current, IDialogService dialogs, ToolChecker tools, AppPaths paths, ILogger logger,
        Func<AppSettings, Task> save, Func<string, bool>? fileExists = null)
    {
        _original = current;
        _dialogs = dialogs;
        _tools = tools;
        _paths = paths;
        _log = logger;
        _saveGlobal = save;
        Title = "Settings";
        Download = new DownloadOptionsViewModel(dialogs, playlistScope: false, fileExists);
        Download.Load(current.Download);
        Download.Edited += UpdateErrors;

        _concurrency = current.Concurrency;
        _videoConcurrency = current.VideoConcurrency;
        _defaultDirectory = current.DefaultDirectory ?? "";
        _defaultVideoDirectory = current.DefaultVideoDirectory ?? "";
        _selectedTheme = Themes.FirstOrDefault(t => t.Value == current.Ui.Theme) ?? Themes[0];
        _selectedLogLevel = LogLevels.FirstOrDefault(l => l.Value == current.Ui.LogLevel) ?? LogLevels[1];
        (_players, _selectedPlayer) = InitialPlayers(current.Playback);
        _playerPath = current.Playback.ExecutablePath ?? "";
        _ = LoadPlayersAsync(current.Playback.DesktopId);
        ToolRows =
        [
            new ToolRowViewModel(this, ToolKind.YtDlp, current.Tools.YtDlpPath,
                "Downloads the playlists and the audio. Required."),
            new ToolRowViewModel(this, ToolKind.Ffmpeg, current.Tools.FfmpegPath,
                "Converts downloads to audio files and embeds covers. Required for downloading."),
            new ToolRowViewModel(this, ToolKind.Ffprobe, null,
                "Used together with ffmpeg. Found next to ffmpeg or on the PATH.", canEditPath: false),
            new ToolRowViewModel(this, ToolKind.JsRuntime, current.Tools.JsRuntimePath,
                "yt-dlp needs one for full YouTube support. deno is preferred; node and bun work too."),
        ];
        UpdateErrors();
    }

    /// <summary>One playlist's own download options. <paramref name="save"/> gets null for "use the global settings".</summary>
    public SettingsViewModel(string playlistName, PlaylistKind kind, DownloadOptions? overrides, AppSettings global,
        IDialogService dialogs, ILogger logger, Func<DownloadOptions?, Task> save, Func<string, bool>? fileExists = null)
    {
        _playlistKind = kind;
        _original = global;
        _dialogs = dialogs;
        _log = logger;
        _savePlaylist = save;
        _playlistOverrides = overrides;
        Title = $"Download settings · {playlistName}";
        Download = new DownloadOptionsViewModel(dialogs, playlistScope: true, fileExists);
        Download.Load(overrides ?? global.Download);
        Download.Edited += UpdateErrors;
        _useCustom = overrides is not null;
        _concurrency = global.Concurrency;
        _videoConcurrency = global.VideoConcurrency;
        _selectedTheme = Themes[0];
        _selectedLogLevel = LogLevels[1];
        ToolRows = [];
        UpdateErrors();
    }

    public string Title { get; }
    public bool IsGlobal => _saveGlobal is not null;
    public bool IsPlaylist => !IsGlobal;

    /// <summary>Playlist scope: the playlist's kind. Null in the global settings, which cover both kinds.</summary>
    private readonly PlaylistKind? _playlistKind;

    /// <summary>Audio tab and the music-only options (artist/title split, square cover, duplicates, replacements).</summary>
    public bool ShowMusicOptions => _playlistKind != PlaylistKind.Video;
    public bool ShowVideoOptions => _playlistKind != PlaylistKind.Music;
    /// <summary>The tab shown first: Video for a video playlist (its Audio tab is hidden), else Audio.</summary>
    public int InitialTab => _playlistKind == PlaylistKind.Video ? 1 : 0;

    public DownloadOptionsViewModel Download { get; }

    /// <summary>Playlist scope: false = the playlist follows the global settings (the options are shown read-only).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(UseGlobal), nameof(IsOptionsEditable))]
    private bool _useCustom;

    public bool UseGlobal
    {
        get => !UseCustom;
        set => UseCustom = !value;
    }

    public bool IsOptionsEditable => IsGlobal || UseCustom;

    [ObservableProperty] private decimal? _concurrency;
    [ObservableProperty] private string? _concurrencyError;
    [ObservableProperty] private decimal? _videoConcurrency;
    [ObservableProperty] private string? _videoConcurrencyError;
    /// <summary>Base folder for new playlists; empty = the system Music folder.</summary>
    [ObservableProperty] private string _defaultDirectory = "";
    [ObservableProperty] private string? _defaultDirectoryError;
    /// <summary>Base folder for new video playlists; empty = the system Videos folder.</summary>
    [ObservableProperty] private string _defaultVideoDirectory = "";
    [ObservableProperty] private string? _defaultVideoDirectoryError;
    [ObservableProperty] private Choice<ThemeMode> _selectedTheme;
    [ObservableProperty] private Choice<LogLevel> _selectedLogLevel;

    // ── Player (App tab) ──

    /// <summary>Choice value of "Other program…" (desktop ids never contain a NUL).</summary>
    private const string CustomPlayerValue = "\0custom";
    private static readonly Choice<string?> DefaultPlayerChoice = new(null, "System default (per file type)");
    private static readonly Choice<string?> CustomPlayerChoice = new(CustomPlayerValue, "Other program…");

    /// <summary>System default, the installed audio players (loaded in the background), and "Other program…".</summary>
    [ObservableProperty] private IReadOnlyList<Choice<string?>> _players = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCustomPlayer))] private Choice<string?>? _selectedPlayer;
    [ObservableProperty] private string _playerPath = "";
    [ObservableProperty] private string? _playerPathError;
    public bool IsCustomPlayer => SelectedPlayer?.Value == CustomPlayerValue;

    partial void OnSelectedPlayerChanged(Choice<string?>? value) => UpdateErrors();
    partial void OnPlayerPathChanged(string value) => UpdateErrors();

    private static (List<Choice<string?>> List, Choice<string?> Selected) InitialPlayers(PlaybackSettings playback)
    {
        var list = new List<Choice<string?>> { DefaultPlayerChoice };
        if (!string.IsNullOrWhiteSpace(playback.DesktopId)) list.Add(new(playback.DesktopId, playback.DesktopId));
        list.Add(CustomPlayerChoice);
        var selected = !string.IsNullOrWhiteSpace(playback.DesktopId) ? list[1]
            : !string.IsNullOrWhiteSpace(playback.ExecutablePath) ? CustomPlayerChoice
            : DefaultPlayerChoice;
        return (list, selected);
    }

    /// <summary>Scans the installed applications off the UI thread and fills in the player list.</summary>
    private async Task LoadPlayersAsync(string? savedId)
    {
        IReadOnlyList<DesktopEntry> found;
        try
        {
            found = await Task.Run(() => PlayerCatalog.FindPlayers(PlayerCatalog.ApplicationDirs()));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Listing the installed players failed");
            return;
        }

        var duplicateNames = found.GroupBy(e => e.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var list = new List<Choice<string?>> { DefaultPlayerChoice };
        list.AddRange(found.Select(e => new Choice<string?>(e.Id, duplicateNames.Contains(e.Name) ? $"{e.Name} ({e.Id})" : e.Name)));
        if (!string.IsNullOrWhiteSpace(savedId) && found.All(e => e.Id != savedId))
            list.Add(new(savedId, $"{savedId} (not found)"));
        list.Add(CustomPlayerChoice);

        // Swapping the items can clear the combo box selection; restore it by value.
        var keep = SelectedPlayer?.Value;
        Players = list;
        SelectedPlayer = list.FirstOrDefault(c => c.Value == keep) ?? DefaultPlayerChoice;
    }

    private PlaybackSettings BuildPlayback() => SelectedPlayer?.Value switch
    {
        null => new PlaybackSettings(),
        CustomPlayerValue => new PlaybackSettings { ExecutablePath = string.IsNullOrWhiteSpace(PlayerPath) ? null : PlayerPath.Trim() },
        var id => new PlaybackSettings { DesktopId = id },
    };

    [RelayCommand]
    private async Task BrowsePlayerAsync()
    {
        var start = string.IsNullOrWhiteSpace(PlayerPath) ? null : System.IO.Path.GetDirectoryName(PlayerPath.Trim());
        var picked = await _dialogs.PickFileAsync("Choose the player program", start);
        if (picked is not null) PlayerPath = picked;
    }

    public IReadOnlyList<ToolRowViewModel> ToolRows { get; }
    [ObservableProperty] private bool _isCheckingTools;

    [ObservableProperty] private string? _errorSummary;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _hasErrors;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _isSaving;
    [ObservableProperty] private string? _saveError;

    public string LogDir => _paths?.LogDir ?? "";
    public string ConfigDir => _paths?.ConfigDir ?? "";

    public event Action<bool>? CloseRequested;

    partial void OnUseCustomChanged(bool value)
    {
        // Switching to custom starts from what is shown: the playlist's earlier options or the global ones.
        if (value) Download.Load(_playlistOverrides ?? _original.Download);
        else Download.Load(_original.Download);
        UpdateErrors();
    }

    partial void OnConcurrencyChanged(decimal? value) => UpdateErrors();
    partial void OnVideoConcurrencyChanged(decimal? value) => UpdateErrors();
    partial void OnDefaultDirectoryChanged(string value) => UpdateErrors();
    partial void OnDefaultVideoDirectoryChanged(string value) => UpdateErrors();

    /// <summary>Shown as the placeholder of the empty default-folder box.</summary>
    public static string MusicFolder => PathUtil.MusicDir;
    public static string VideosFolder => PathUtil.VideosDir;

    internal void OnToolPathChanged() => UpdateErrors();

    private AppSettings Build() => _original with
    {
        Download = Download.ToOptions(),
        Concurrency = Concurrency is { } c ? (int)Math.Round(c) : 0,
        VideoConcurrency = VideoConcurrency is { } vc ? (int)Math.Round(vc) : 0,
        DefaultDirectory = string.IsNullOrWhiteSpace(DefaultDirectory) ? null : DefaultDirectory.Trim(),
        DefaultVideoDirectory = string.IsNullOrWhiteSpace(DefaultVideoDirectory) ? null : DefaultVideoDirectory.Trim(),
        Tools = _original.Tools with
        {
            YtDlpPath = Row(ToolKind.YtDlp)?.ConfiguredPath ?? null,
            FfmpegPath = Row(ToolKind.Ffmpeg)?.ConfiguredPath ?? null,
            JsRuntimePath = Row(ToolKind.JsRuntime)?.ConfiguredPath ?? null,
        },
        Ui = _original.Ui with { Theme = SelectedTheme.Value, LogLevel = SelectedLogLevel.Value },
        Playback = IsGlobal ? BuildPlayback() : _original.Playback,
    };

    private ToolRowViewModel? Row(ToolKind kind) => ToolRows.FirstOrDefault(r => r.Kind == kind);

    private void UpdateErrors()
    {
        var fields = new List<string>(Download.IsPlaylistScope && !UseCustom ? [] : Download.Validation.Errors.Keys);
        if (IsGlobal)
        {
            var app = SettingsValidator.ValidateApp(Build());
            ConcurrencyError = app.Error(nameof(AppSettings.Concurrency));
            VideoConcurrencyError = app.Error(nameof(AppSettings.VideoConcurrency));
            DefaultDirectoryError = app.Error(nameof(AppSettings.DefaultDirectory));
            DefaultVideoDirectoryError = app.Error(nameof(AppSettings.DefaultVideoDirectory));
            PlayerPathError = app.Error(nameof(PlaybackSettings.ExecutablePath));
            foreach (var row in ToolRows)
                row.PathError = row.SettingsKey is { } key ? app.Error(key) : null;
            fields.AddRange(app.Errors.Keys);
        }

        HasErrors = fields.Count > 0;
        var tabs = fields.Select(f => FieldTabs.GetValueOrDefault(f, "Audio")).Distinct().ToList();
        ErrorSummary = HasErrors ? $"Fix the marked fields first ({string.Join(", ", tabs)})." : null;
    }

    private bool CanSave => !HasErrors && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        UpdateErrors();
        if (!CanSave) return;
        IsSaving = true;
        SaveError = null;
        try
        {
            if (_saveGlobal is not null) await _saveGlobal(Build());
            else await _savePlaylist!(UseCustom ? Download.ToOptions() : null);
            _toolCheck?.Cancel();
            CloseRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Saving settings failed");
            SaveError = "Could not save: " + ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _toolCheck?.Cancel();
        CloseRequested?.Invoke(false);
    }

    /// <summary>Restores the defaults of the download options (the network group is kept).</summary>
    [RelayCommand]
    private void ResetDownloadDefaults()
    {
        var current = Download.ToOptions();
        Download.Load(new DownloadOptions().WithNetworkFrom(current) with { ExtraArgs = current.ExtraArgs });
    }

    [RelayCommand]
    private async Task CheckToolsAsync()
    {
        if (_tools is null) return;
        _toolCheck?.Cancel();
        var cts = new CancellationTokenSource();
        _toolCheck = cts;
        IsCheckingTools = true;
        foreach (var row in ToolRows) row.SetChecking();
        try
        {
            // Check what is typed in, not what is saved, so a new path can be tried before saving.
            var settings = Build().Tools;
            var results = await Task.Run(() => _tools.CheckAllAsync(settings, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var status in results) Row(status.Kind)?.SetStatus(status);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Checking the tools failed");
            foreach (var row in ToolRows) row.SetFailed(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_toolCheck, cts)) IsCheckingTools = false;
        }
    }

    internal async Task BrowseToolAsync(ToolRowViewModel row)
    {
        var start = string.IsNullOrWhiteSpace(row.Path) ? null : System.IO.Path.GetDirectoryName(row.Path.Trim());
        var picked = await _dialogs.PickFileAsync($"Choose the {ToolChecker.DisplayName(row.Kind)} executable", start);
        if (picked is null) return;
        row.Path = picked;
        await CheckToolsAsync();
    }

    [RelayCommand]
    private async Task BrowseDefaultDirectoryAsync()
    {
        var picked = await PickBaseFolderAsync(DefaultDirectory, "Choose the base folder for new music playlists", PathUtil.MusicDir);
        if (picked is not null) DefaultDirectory = picked;
    }

    [RelayCommand]
    private async Task BrowseDefaultVideoDirectoryAsync()
    {
        var picked = await PickBaseFolderAsync(DefaultVideoDirectory, "Choose the base folder for new video playlists", PathUtil.VideosDir);
        if (picked is not null) DefaultVideoDirectory = picked;
    }

    private Task<string?> PickBaseFolderAsync(string current, string title, string fallback)
    {
        var start = PathUtil.NormalizeUserPath(current, PathUtil.Home);
        if (PlaylistSync.Core.Locations.FolderResolver.IsRemote(start)) start = null; // the picker needs a local start folder
        while (start is not null && !Directory.Exists(start))
            start = System.IO.Path.GetDirectoryName(start);
        return _dialogs.PickFolderAsync(title, start ?? fallback);
    }

    [RelayCommand]
    private async Task OpenLogFolderAsync()
    {
        if (_paths is null) return;
        Directory.CreateDirectory(_paths.LogDir);
        await _dialogs.OpenFolderAsync(_paths.LogDir);
    }

    [RelayCommand]
    private async Task OpenConfigFolderAsync()
    {
        if (_paths is null) return;
        await _dialogs.OpenFolderAsync(_paths.ConfigDir);
    }
}

/// <summary>One external tool in the Tools tab: optional path, and the result of the last check.</summary>
public sealed partial class ToolRowViewModel : ViewModelBase
{
    private readonly SettingsViewModel _owner;

    public ToolRowViewModel(SettingsViewModel owner, ToolKind kind, string? configuredPath, string description, bool canEditPath = true)
    {
        _owner = owner;
        Kind = kind;
        Description = description;
        CanEditPath = canEditPath;
        _path = configuredPath ?? "";
    }

    public ToolKind Kind { get; }
    public string Name => ToolChecker.DisplayName(Kind);
    public string Description { get; }
    public bool CanEditPath { get; }

    /// <summary>Property name in <see cref="ToolSettings"/>; null for tools without a path setting.</summary>
    public string? SettingsKey => Kind switch
    {
        ToolKind.YtDlp => nameof(ToolSettings.YtDlpPath),
        ToolKind.Ffmpeg => nameof(ToolSettings.FfmpegPath),
        ToolKind.JsRuntime => nameof(ToolSettings.JsRuntimePath),
        _ => null,
    };

    [ObservableProperty] private string _path;
    [ObservableProperty] private string? _pathError;
    [ObservableProperty] private string _statusText = "Not checked yet";
    [ObservableProperty] private bool _isOk;
    [ObservableProperty] private bool _isError;

    public string? ConfiguredPath => CanEditPath && !string.IsNullOrWhiteSpace(Path) ? Path.Trim() : null;

    partial void OnPathChanged(string value) => _owner.OnToolPathChanged();

    [RelayCommand]
    private Task BrowseAsync() => _owner.BrowseToolAsync(this);

    [RelayCommand]
    private void ClearPath() => Path = "";

    internal void SetChecking()
    {
        StatusText = "Checking…";
        IsOk = false;
        IsError = false;
    }

    internal void SetStatus(ToolStatus status)
    {
        IsOk = status.IsUsable;
        // A missing JS runtime only limits yt-dlp; it is not an error like a missing yt-dlp.
        IsError = !status.IsUsable && Kind != ToolKind.JsRuntime;
        if (status.IsUsable)
        {
            var loc = status.Location!;
            var source = loc.Source switch
            {
                ToolSource.Configured => "your path",
                ToolSource.Managed => "downloaded by Playlist Sync",
                _ => "system",
            };
            var name = Kind == ToolKind.JsRuntime ? $"{loc.Name} " : "";
            StatusText = $"✓ {name}{status.Version} · {loc.Path} ({source})";
        }
        else
        {
            StatusText = status.Error ?? "Not usable.";
        }
    }

    internal void SetFailed(string message)
    {
        IsOk = false;
        IsError = true;
        StatusText = "Check failed: " + message;
    }
}
