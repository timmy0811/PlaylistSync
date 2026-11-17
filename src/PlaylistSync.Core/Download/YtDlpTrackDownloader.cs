using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Covers;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Download;

/// <summary>The tools one download needs, resolved once per sync.</summary>
public sealed record DownloadTools(ToolLocation YtDlp, ToolLocation? Ffmpeg, ToolLocation? JsRuntime);

/// <param name="TargetDirectory">The playlist folder the finished file is moved into.</param>
/// <param name="TempRoot">Parent of the per-track temp dirs (<c>&lt;TempRoot&gt;/&lt;videoId&gt;</c>).</param>
public sealed record DownloadRequest(
    string VideoId,
    string TargetDirectory,
    string TempRoot,
    DownloadOptions Options,
    DownloadTools Tools)
{
    /// <summary>1-based remote position, for the optional filename prefix.</summary>
    public int? TrackNumber { get; init; }
    public int TrackNumberWidth { get; init; } = 2;
    /// <summary>
    /// The video actually downloaded, when it differs from <see cref="VideoId"/> (a replacement for a gone video).
    /// Temp dir and file naming stay keyed by <see cref="VideoId"/>.
    /// </summary>
    public string? SourceVideoId { get; init; }

    public string EffectiveSourceId => SourceVideoId ?? VideoId;

    public string TrackTempDirectory => Path.Combine(TempRoot, VideoId);
}

public enum DownloadErrorKind
{
    Other,
    /// <summary>yt-dlp or ffmpeg could not be started.</summary>
    ToolMissing,
    /// <summary>The video is private, deleted or otherwise gone: <see cref="TrackStatus.Unavailable"/>, not a failure.</summary>
    Unavailable,
    /// <summary>Age-restricted, members-only or a bot check: cookies are needed.</summary>
    NeedsCookies,
}

/// <param name="Message">User-facing.</param>
public sealed record DownloadError(DownloadErrorKind Kind, string Message);

/// <param name="File">Path relative to the playlist folder with <c>/</c> separators; null on failure.</param>
public sealed record DownloadResult(string? File, DownloadError? Error)
{
    public bool Succeeded => File is not null;
    public static DownloadResult Success(string file) => new(file, null);
    public static DownloadResult Failure(DownloadErrorKind kind, string message) => new(null, new DownloadError(kind, message));
}

public interface ITrackDownloader
{
    /// <summary>
    /// Downloads one track into <see cref="DownloadRequest.TargetDirectory"/>. Failures are returned, not thrown;
    /// only cancellation throws (<see cref="OperationCanceledException"/>, the process tree is killed).
    /// </summary>
    Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken ct);
}

/// <summary>
/// Runs one yt-dlp process per track into a per-track temp dir, then moves the file into the playlist folder.
/// Nothing half-written ever appears in the playlist folder; a failed or cancelled attempt leaves its temp dir
/// so yt-dlp can resume the <c>.part</c> file next time.
/// </summary>
/// <param name="thumbnails">Makes the track list image from the video thumbnail; without it none is kept.</param>
public sealed class YtDlpTrackDownloader(
    IProcessRunner runner,
    ILogger<YtDlpTrackDownloader> log,
    ITrackThumbnails? thumbnails = null) : ITrackDownloader
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var tempDir = request.TrackTempDirectory;
        try
        {
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DownloadResult.Failure(DownloadErrorKind.Other, $"Could not create the temp folder '{tempDir}': {ex.Message}");
        }

        var ffmpeg = request.Tools.Ffmpeg is { Exists: true } f ? f : null;
        var keepThumbnail = thumbnails is not null && ffmpeg is not null;
        var args = YtDlpArgsBuilder.TrackDownload(request.EffectiveSourceId, tempDir, request.Options,
            request.Tools.Ffmpeg, request.Tools.JsRuntime, request.TrackNumber, request.TrackNumberWidth,
            OperatingSystem.IsWindows(), keepThumbnail);

        string? finalPath = null;
        var throttle = new ProgressThrottle(progress);
        throttle.Report(new DownloadProgress(DownloadPhase.Starting));

        var processRequest = new ProcessRequest(request.Tools.YtDlp.Path, args)
        {
            Environment = YtDlpArgsBuilder.Environment,
            CaptureStdout = false,
            OnStdoutLine = line =>
            {
                if (DownloadOutputParser.ParseDownload(line) is { } p) throttle.Report(p);
                else if (DownloadOutputParser.ParseFile(line) is { } file) finalPath = file;
                else log.LogDebug("yt-dlp [{Id}]: {Line}", request.VideoId, line);
            },
            OnStderrLine = line =>
            {
                if (DownloadOutputParser.ParsePostProcess(line) is { } p) throttle.Report(p, force: true);
                else if (!line.StartsWith(DownloadOutputParser.PostProcessMarker, StringComparison.Ordinal))
                    log.LogDebug("yt-dlp [{Id}]: {Line}", request.VideoId, line);
            },
        };

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(processRequest, ct).ConfigureAwait(false);
        }
        catch (ProcessStartException ex)
        {
            return DownloadResult.Failure(DownloadErrorKind.ToolMissing, ex.Message);
        }

        if (!result.Succeeded)
        {
            var error = DescribeError(result.StandardError);
            log.LogWarning("Download of {Id} failed (exit {Exit}): {Error}", request.EffectiveSourceId, result.ExitCode, error.Message);
            return new DownloadResult(null, error);
        }

        if (finalPath is null || !File.Exists(finalPath))
        {
            log.LogWarning("yt-dlp finished {Id} but reported no file (reported: {Path})", request.VideoId, finalPath);
            return DownloadResult.Failure(DownloadErrorKind.Other, "yt-dlp finished but did not produce an audio file.");
        }

        string relative;
        try
        {
            relative = FilePlacer.MoveIntoPlace(finalPath, tempDir, request.TargetDirectory, request.VideoId,
                request.Options.RestrictFilenames);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not move {File} into {Dir}", finalPath, request.TargetDirectory);
            return DownloadResult.Failure(DownloadErrorKind.Other, $"Could not move the file into the playlist folder: {ex.Message}");
        }

        // After the file is in place the track counts as done, so cancelling must not undo it: the thumbnail is
        // best effort with its own short timeout instead of the sync's token.
        if (keepThumbnail) await thumbnails!.CreateFromTempDirAsync(request.VideoId, tempDir, ffmpeg!, CancellationToken.None).ConfigureAwait(false);

        TryDeleteDirectory(tempDir);
        log.LogInformation("Downloaded {Id} → {File} in {Elapsed:0.0}s", request.VideoId, relative, result.Duration.TotalSeconds);
        return DownloadResult.Success(relative);
    }

    /// <summary>Classifies yt-dlp's stderr: gone videos become Unavailable, sign-in walls get the cookies hint.</summary>
    internal static DownloadError DescribeError(string stderr)
    {
        var message = YtDlpErrorText.LastError(stderr);

        // YouTube's rate-limit page also says "Video unavailable", so check this first.
        if (YtDlpErrorText.ContainsAny(message, "try again later", "HTTP Error 429", "rate-limited", "rate limited"))
        {
            return new DownloadError(DownloadErrorKind.Other,
                $"{message}\nYouTube is rate-limiting this connection. Wait a while, or set a sleep interval in Settings → Network.");
        }
        // "Private video. Sign in if you've been granted access…": private means unavailable (PLAN §10.4), despite the "Sign in".
        if (YtDlpErrorText.ContainsAny(message, "Private video"))
            return new DownloadError(DownloadErrorKind.Unavailable, message);
        if (YtDlpErrorText.NeedsCookies(message))
            return new DownloadError(DownloadErrorKind.NeedsCookies, $"{message}\n{YtDlpErrorText.CookiesHint}");
        if (YtDlpErrorText.ContainsAny(message, "This video is unavailable", "Video unavailable",
                "has been removed", "no longer available", "account associated with this video has been terminated",
                "This video does not exist"))
        {
            return new DownloadError(DownloadErrorKind.Unavailable, message);
        }
        if (YtDlpErrorText.ContainsAny(message, "ffmpeg not found", "ffprobe and ffmpeg not found", "ffprobe not found"))
            return new DownloadError(DownloadErrorKind.ToolMissing, $"{message}\nInstall ffmpeg or set its path in Settings → Tools.");
        if (message.Contains("mutagen", StringComparison.OrdinalIgnoreCase))
        {
            return new DownloadError(DownloadErrorKind.Other,
                $"{message}\nEmbedding covers in this format needs the Python package mutagen (Arch: python-mutagen), or turn off \"Embed cover\".");
        }
        return new DownloadError(DownloadErrorKind.Other, message);
    }

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            // Our own temp dir: only yt-dlp intermediates (source stream, thumbnail) are left in it.
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Could not delete temp dir {Dir}", dir);
        }
    }

    /// <summary>yt-dlp reports several times per second; pass on at most one update per interval, plus phase changes.</summary>
    internal sealed class ProgressThrottle(IProgress<DownloadProgress>? target)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private DownloadPhase? _lastPhase;
        private TimeSpan _last;

        // stdout and stderr are pumped on different threads.
        public void Report(DownloadProgress p, bool force = false)
        {
            if (target is null) return;
            lock (_clock)
            {
                var now = _clock.Elapsed;
                if (!force && p.Phase == _lastPhase && p.Percent is not 100 && now - _last < ProgressInterval) return;
                _lastPhase = p.Phase;
                _last = now;
                target.Report(p);
            }
        }
    }
}
