using Microsoft.Extensions.Logging;

namespace PlaylistSync.Core.Models;

public sealed record AppSettings
{
    public DownloadOptions Download { get; init; } = new();
    public ToolSettings Tools { get; init; } = new();
    public UiSettings Ui { get; init; } = new();
    /// <summary>How many tracks download in parallel within one playlist sync.</summary>
    public int Concurrency { get; init; } = 2;
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
}
