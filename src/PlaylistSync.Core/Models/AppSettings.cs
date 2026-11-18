using Microsoft.Extensions.Logging;

namespace PlaylistSync.Core.Models;

public sealed record AppSettings
{
    public DownloadOptions Download { get; init; } = new();
    public ToolSettings Tools { get; init; } = new();
    public UiSettings Ui { get; init; } = new();
    public PlaybackSettings Playback { get; init; } = new();
    public ToolboxSettings Toolbox { get; init; } = new();
    /// <summary>How many tracks of a music playlist download in parallel within one sync.</summary>
    public int Concurrency { get; init; } = 2;
    /// <summary>How many videos of a video playlist download in parallel within one sync (videos are large; default 1).</summary>
    public int VideoConcurrency { get; init; } = 1;

    /// <summary>The parallel downloads for a playlist of <paramref name="kind"/>.</summary>
    public int ConcurrencyFor(PlaylistKind kind) => kind == PlaylistKind.Video ? VideoConcurrency : Concurrency;
    /// <summary>
    /// Base folder for new playlists: the suggested folder is <c>&lt;DefaultDirectory&gt;/&lt;playlist title&gt;</c>.
    /// A leading <c>~</c> means the home folder. Null = the system Music folder.
    /// </summary>
    public string? DefaultDirectory { get; init; }
    /// <summary>Like <see cref="DefaultDirectory"/>, for video playlists. Null = the system Videos folder.</summary>
    public string? DefaultVideoDirectory { get; init; }
}

public sealed record ToolSettings
{
    /// <summary>Explicit path; null = auto-detect (configured → managed bin dir → PATH).</summary>
    public string? YtDlpPath { get; init; }
    public string? FfmpegPath { get; init; }
    public string? JsRuntimePath { get; init; }
}

public sealed record UiSettings
{
    public ThemeMode Theme { get; init; } = ThemeMode.System;
    public LogLevel LogLevel { get; init; } = LogLevel.Information;
    public double? WindowWidth { get; init; }
    public double? WindowHeight { get; init; }
    /// <summary>Which playlists the sidebar shows (music or video), remembered across restarts.</summary>
    public PlaylistKind SidebarKind { get; init; } = PlaylistKind.Music;
    /// <summary>The sidebar also lists hidden playlists (dimmed).</summary>
    public bool ShowHidden { get; init; }
}

/// <summary>Which program Play / Shuffle open tracks with. Both null = the system default for each file type.</summary>
public sealed record PlaybackSettings
{
    /// <summary>Desktop file id of an installed player (Linux), e.g. <c>org.kde.haruna.desktop</c>. Wins over the path.</summary>
    public string? DesktopId { get; init; }
    /// <summary>Full path of a player program; the file to play is passed as its only argument.</summary>
    public string? ExecutablePath { get; init; }

    public bool IsSystemDefault => string.IsNullOrWhiteSpace(DesktopId) && string.IsNullOrWhiteSpace(ExecutablePath);
}

/// <summary>What the toolbox's single download used last time, so it opens the same way.</summary>
public sealed record ToolboxSettings
{
    /// <summary>Last target folder; null = the system Downloads/Music folder.</summary>
    public string? Folder { get; init; }
    public bool Video { get; init; }
    public AudioFormat AudioFormat { get; init; } = AudioFormat.Mp3;
    /// <summary>Preferred maximum video height; null = best available.</summary>
    public int? MaxHeight { get; init; } = 1080;
}
