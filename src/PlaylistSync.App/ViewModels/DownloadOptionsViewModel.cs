using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlaylistSync.App.Services;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Settings;

namespace PlaylistSync.App.ViewModels;

/// <summary>
/// Editable copy of <see cref="DownloadOptions"/> for the settings dialog (global defaults or one playlist's own options).
/// Validated on every change with <see cref="SettingsValidator"/>.
/// </summary>
public sealed partial class DownloadOptionsViewModel : ViewModelBase
{
    public static readonly IReadOnlyList<Choice<AudioFormat>> Formats =
    [
        new(AudioFormat.Mp3, "MP3"),
        new(AudioFormat.M4a, "M4A (AAC)"),
        new(AudioFormat.Opus, "Opus"),
        new(AudioFormat.Ogg, "Ogg Vorbis"),
        new(AudioFormat.Flac, "FLAC (lossless container)"),
        new(AudioFormat.Wav, "WAV (uncompressed)"),
        new(AudioFormat.Best, "Original (no re-encoding)"),
    ];

    public static readonly IReadOnlyList<int> Bitrates = [96, 128, 160, 192, 256, 320];

    /// <summary>Maximum video resolutions; 0 = best available (stored as null).</summary>
    public static readonly IReadOnlyList<Choice<int>> VideoHeights =
    [
        new(0, "Best available"),
        new(2160, "2160p (4K)"),
        new(1440, "1440p"),
        new(1080, "1080p (Full HD)"),
        new(720, "720p"),
        new(480, "480p"),
        new(360, "360p"),
    ];

    public static readonly IReadOnlyList<Choice<VideoContainer>> Containers =
    [
        new(VideoContainer.Mp4, "MP4 (plays everywhere)"),
        new(VideoContainer.Mkv, "MKV (any streams, no re-encoding)"),
    ];

    public static readonly IReadOnlyList<Choice<CookieMode>> CookieModes =
    [
        new(CookieMode.None, "No cookies"),
        new(CookieMode.File, "cookies.txt file"),
        new(CookieMode.Browser, "From a browser"),
    ];

    public static IReadOnlyList<string> Browsers => SettingsValidator.CookieBrowsers;

    private readonly IDialogService _dialogs;
    private readonly Func<string, bool> _fileExists;
    private DownloadOptions _base = new();
    private bool _loading;

    public DownloadOptionsViewModel(IDialogService dialogs, bool playlistScope, Func<string, bool>? fileExists = null)
    {
        _dialogs = dialogs;
        IsPlaylistScope = playlistScope;
        _fileExists = fileExists ?? File.Exists;
        Load(new DownloadOptions());
    }

    /// <summary>One playlist's options: the network group is hidden (it always comes from the global settings).</summary>
    public bool IsPlaylistScope { get; }

    // Audio
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasQuality), nameof(QualityHint))] private Choice<AudioFormat> _format = Formats[0];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCbr))] private bool _isVbr = true;
    [ObservableProperty] private decimal? _vbrLevel;
    [ObservableProperty] private int _cbrKbps = 320;

    public bool IsCbr
    {
        get => !IsVbr;
        set => IsVbr = !value;
    }

    public bool HasQuality => Format.Value is not (AudioFormat.Flac or AudioFormat.Wav or AudioFormat.Best);

    public string QualityHint => Format.Value switch
    {
        AudioFormat.Flac => "FLAC is lossless, but the source is YouTube's compressed audio: files get larger, not better.",
        AudioFormat.Wav => "WAV is uncompressed and can't hold tags or a cover reliably.",
        AudioFormat.Best => "Keeps YouTube's audio as it is (usually Opus or M4A). Fastest, no quality loss from converting.",
        _ => "VBR 0 is the best quality. Converting can't add quality the source doesn't have.",
    };

    // Video
    [ObservableProperty] private Choice<int> _videoHeight = VideoHeights[3];
    [ObservableProperty] private Choice<VideoContainer> _selectedContainer = Containers[0];
    [ObservableProperty] private bool _embedSubtitles;
    [ObservableProperty] private string _subtitleLanguages = DownloadOptions.DefaultSubtitleLanguages;
    [ObservableProperty] private string _videoFilenameTemplate = "";

    // Metadata
    [ObservableProperty] private bool _embedMetadata;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CoverHint))] private bool _embedThumbnail;
    [ObservableProperty] private bool _cropThumbnailSquare;
    [ObservableProperty] private bool _embedChapters;
    [ObservableProperty] private bool _parseArtistTitle;

    public string? CoverHint => EmbedThumbnailWarning ?? (EmbedThumbnail && Format.Value is AudioFormat.Opus or AudioFormat.Ogg or AudioFormat.Flac
        ? "Covers in Opus, Ogg and FLAC files need yt-dlp's mutagen module (included in the official yt-dlp builds)."
        : null);

    // Files
    [ObservableProperty] private string _filenameTemplate = "";
    [ObservableProperty] private bool _restrictFilenames;
    [ObservableProperty] private bool _trackNumberPrefix;
    [ObservableProperty] private bool _writeM3u;
    [ObservableProperty] private bool _saveFolderCover;

    // Auth / network
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCookieFile), nameof(IsCookieBrowser))] private Choice<CookieMode> _selectedCookieMode = CookieModes[0];
    [ObservableProperty] private string? _cookiesFile;
    [ObservableProperty] private string? _cookiesBrowser;
    [ObservableProperty] private string? _cookiesBrowserProfile;
    [ObservableProperty] private string? _proxy;
    [ObservableProperty] private string? _rateLimit;
    [ObservableProperty] private decimal? _sleepIntervalSeconds;
    [ObservableProperty] private decimal? _retries;

    public bool IsCookieFile => SelectedCookieMode.Value == CookieMode.File;
    public bool IsCookieBrowser => SelectedCookieMode.Value == CookieMode.Browser;

    // Extras
    [ObservableProperty] private bool _sponsorBlockRemoveOfftopic;
    [ObservableProperty] private bool _redownloadMissing;
    [ObservableProperty] private bool _findReplacements;
    [ObservableProperty] private bool _skipDuplicates;
    [ObservableProperty] private string? _extraArgs;

    // Validation
    [ObservableProperty] private string? _vbrLevelError;
    [ObservableProperty] private string? _filenameTemplateError;
    [ObservableProperty] private string? _videoFilenameTemplateError;
    [ObservableProperty] private string? _subtitleLanguagesError;
    [ObservableProperty] private string? _cookiesFileError;
    [ObservableProperty] private string? _cookiesBrowserError;
    [ObservableProperty] private string? _proxyError;
    [ObservableProperty] private string? _rateLimitError;
    [ObservableProperty] private string? _sleepIntervalSecondsError;
    [ObservableProperty] private string? _retriesError;
    [ObservableProperty] private string? _extraArgsError;
    [ObservableProperty] private string? _extraArgsWarning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CoverHint))] private string? _embedThumbnailWarning;

    public SettingsValidation Validation { get; private set; } = new(new Dictionary<string, string>(), new Dictionary<string, string>());

    /// <summary>Raised after every edit (and after <see cref="Load"/>), once validation has run.</summary>
    public event Action? Edited;

    public void Load(DownloadOptions o)
    {
        _loading = true;
        try
        {
            _base = o;
            Format = Formats.FirstOrDefault(f => f.Value == o.Format) ?? Formats[0];
            IsVbr = o.QualityMode == QualityMode.Vbr;
            VbrLevel = o.VbrLevel;
            CbrKbps = Bitrates.Contains(o.CbrKbps) ? o.CbrKbps : Bitrates.MinBy(b => Math.Abs(b - o.CbrKbps));
            VideoHeight = o.VideoMaxHeight is { } h
                ? VideoHeights.Where(c => c.Value > 0).MinBy(c => Math.Abs(c.Value - h))!
                : VideoHeights[0];
            SelectedContainer = Containers.FirstOrDefault(c => c.Value == o.VideoContainer) ?? Containers[0];
            EmbedSubtitles = o.EmbedSubtitles;
            SubtitleLanguages = string.IsNullOrWhiteSpace(o.SubtitleLanguages) ? DownloadOptions.DefaultSubtitleLanguages : o.SubtitleLanguages;
            VideoFilenameTemplate = string.IsNullOrWhiteSpace(o.VideoFilenameTemplate)
                ? DownloadOptions.DefaultVideoFilenameTemplate
                : o.VideoFilenameTemplate;
            EmbedMetadata = o.EmbedMetadata;
            EmbedThumbnail = o.EmbedThumbnail;
            CropThumbnailSquare = o.CropThumbnailSquare;
            EmbedChapters = o.EmbedChapters;
            ParseArtistTitle = o.ParseArtistTitle;
            FilenameTemplate = string.IsNullOrWhiteSpace(o.FilenameTemplate) ? DownloadOptions.DefaultFilenameTemplate : o.FilenameTemplate;
            RestrictFilenames = o.RestrictFilenames;
            TrackNumberPrefix = o.TrackNumberPrefix;
            WriteM3u = o.WriteM3u;
            SaveFolderCover = o.SaveFolderCover;
            SelectedCookieMode = CookieModes.FirstOrDefault(c => c.Value == o.CookieMode) ?? CookieModes[0];
            CookiesFile = o.CookiesFile;
            CookiesBrowser = o.CookiesBrowser;
            CookiesBrowserProfile = o.CookiesBrowserProfile;
            Proxy = o.Proxy;
            RateLimit = o.RateLimit;
            SleepIntervalSeconds = o.SleepIntervalSeconds;
            Retries = o.Retries;
            SponsorBlockRemoveOfftopic = o.SponsorBlockRemoveOfftopic;
            RedownloadMissing = o.RedownloadMissing;
            FindReplacements = o.FindReplacements;
            SkipDuplicates = o.SkipDuplicates;
            ExtraArgs = o.ExtraArgs;
        }
        finally
        {
            _loading = false;
        }
        Validate();
    }

    public DownloadOptions ToOptions() => _base with
    {
        Format = Format.Value,
        QualityMode = IsVbr ? QualityMode.Vbr : QualityMode.Cbr,
        VbrLevel = ToInt(VbrLevel, -1),
        CbrKbps = CbrKbps,
        VideoMaxHeight = VideoHeight.Value > 0 ? VideoHeight.Value : null,
        VideoContainer = SelectedContainer.Value,
        EmbedSubtitles = EmbedSubtitles,
        SubtitleLanguages = string.IsNullOrWhiteSpace(SubtitleLanguages) ? DownloadOptions.DefaultSubtitleLanguages : SubtitleLanguages.Trim(),
        VideoFilenameTemplate = string.IsNullOrWhiteSpace(VideoFilenameTemplate)
            ? DownloadOptions.DefaultVideoFilenameTemplate
            : VideoFilenameTemplate.Trim(),
        EmbedMetadata = EmbedMetadata,
        EmbedThumbnail = EmbedThumbnail,
        CropThumbnailSquare = CropThumbnailSquare,
        EmbedChapters = EmbedChapters,
        ParseArtistTitle = ParseArtistTitle,
        FilenameTemplate = string.IsNullOrWhiteSpace(FilenameTemplate) ? DownloadOptions.DefaultFilenameTemplate : FilenameTemplate.Trim(),
        RestrictFilenames = RestrictFilenames,
        TrackNumberPrefix = TrackNumberPrefix,
        WriteM3u = WriteM3u,
        SaveFolderCover = SaveFolderCover,
        CookieMode = SelectedCookieMode.Value,
        CookiesFile = Trimmed(CookiesFile),
        CookiesBrowser = Trimmed(CookiesBrowser)?.ToLowerInvariant(),
        CookiesBrowserProfile = Trimmed(CookiesBrowserProfile),
        Proxy = Trimmed(Proxy),
        RateLimit = Trimmed(RateLimit),
        SleepIntervalSeconds = ToInt(SleepIntervalSeconds, -1),
        Retries = ToInt(Retries, -1),
        SponsorBlockRemoveOfftopic = SponsorBlockRemoveOfftopic,
        RedownloadMissing = RedownloadMissing,
        FindReplacements = FindReplacements,
        SkipDuplicates = SkipDuplicates,
        ExtraArgs = Trimmed(ExtraArgs),
    };

    [RelayCommand]
    private void ResetTemplate() => FilenameTemplate = DownloadOptions.DefaultFilenameTemplate;

    [RelayCommand]
    private void ResetVideoTemplate() => VideoFilenameTemplate = DownloadOptions.DefaultVideoFilenameTemplate;

    [RelayCommand]
    private async Task BrowseCookiesFileAsync()
    {
        var start = string.IsNullOrWhiteSpace(CookiesFile) ? null : Path.GetDirectoryName(CookiesFile.Trim());
        var picked = await _dialogs.PickFileAsync("Choose a cookies.txt file", start);
        if (picked is not null) CookiesFile = picked;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null || IsDerived(e.PropertyName)) return;
        Validate();
    }

    private static bool IsDerived(string name) =>
        name.EndsWith("Error", StringComparison.Ordinal) || name.EndsWith("Warning", StringComparison.Ordinal) ||
        name.EndsWith("Hint", StringComparison.Ordinal) ||
        name is nameof(Validation) or nameof(HasQuality) or nameof(IsCbr) or nameof(IsCookieFile) or nameof(IsCookieBrowser);

    private void Validate()
    {
        var o = ToOptions();
        var v = SettingsValidator.ValidateDownload(o, _fileExists, IsPlaylistScope);
        // Empty number boxes become -1 above, which the range checks report.
        VbrLevelError = v.Error(nameof(DownloadOptions.VbrLevel));
        FilenameTemplateError = v.Error(nameof(DownloadOptions.FilenameTemplate));
        VideoFilenameTemplateError = v.Error(nameof(DownloadOptions.VideoFilenameTemplate));
        SubtitleLanguagesError = v.Error(nameof(DownloadOptions.SubtitleLanguages));
        CookiesFileError = v.Error(nameof(DownloadOptions.CookiesFile));
        CookiesBrowserError = v.Error(nameof(DownloadOptions.CookiesBrowser));
        ProxyError = v.Error(nameof(DownloadOptions.Proxy));
        RateLimitError = v.Error(nameof(DownloadOptions.RateLimit));
        SleepIntervalSecondsError = v.Error(nameof(DownloadOptions.SleepIntervalSeconds));
        RetriesError = v.Error(nameof(DownloadOptions.Retries));
        ExtraArgsError = v.Error(nameof(DownloadOptions.ExtraArgs));
        ExtraArgsWarning = v.Warning(nameof(DownloadOptions.ExtraArgs));
        EmbedThumbnailWarning = v.Warning(nameof(DownloadOptions.EmbedThumbnail));
        Validation = v;
        OnPropertyChanged(nameof(Validation));
        OnPropertyChanged(nameof(CoverHint));
        Edited?.Invoke();
    }

    private static int ToInt(decimal? value, int empty) =>
        value is { } d ? (int)Math.Round(d, MidpointRounding.AwayFromZero) : empty;

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Short description for the playlist editor, e.g. "Opus · VBR 0", "FLAC" or "MP4 · up to 1080p".</summary>
    public static string Describe(DownloadOptions o, PlaylistKind kind = PlaylistKind.Music)
    {
        if (kind == PlaylistKind.Video)
        {
            var container = o.VideoContainer == VideoContainer.Mkv ? "MKV" : "MP4";
            return o.VideoMaxHeight is { } h
                ? $"{container} · up to {h.ToString(CultureInfo.InvariantCulture)}p"
                : $"{container} · best quality";
        }
        var format = Formats.FirstOrDefault(f => f.Value == o.Format)?.Label.Split(' ')[0] ?? o.Format.ToString();
        if (o.Format is AudioFormat.Flac or AudioFormat.Wav or AudioFormat.Best) return format;
        return o.QualityMode == QualityMode.Vbr
            ? $"{format} · VBR {o.VbrLevel.ToString(CultureInfo.InvariantCulture)}"
            : $"{format} · {o.CbrKbps.ToString(CultureInfo.InvariantCulture)} kbps";
    }
}
