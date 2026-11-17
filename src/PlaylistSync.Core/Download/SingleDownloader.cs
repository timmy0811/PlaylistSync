using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Locations;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Download;

public enum SingleDownloadKind { Audio, Video }

/// <param name="Url">Any URL yt-dlp supports (a YouTube video, SoundCloud track, …).</param>
/// <param name="OutputDirectory">Local folder the finished file is saved in.</param>
/// <param name="TempDirectory">Local folder for intermediate files (<c>.part</c>, source streams).</param>
/// <param name="AudioFormat">Target format for <see cref="SingleDownloadKind.Audio"/>.</param>
/// <param name="MaxHeight">Preferred maximum video height for <see cref="SingleDownloadKind.Video"/>; null = best.</param>
/// <param name="Options">Global download options: audio quality, tagging, filename template, cookies, network.</param>
public sealed record SingleDownloadSpec(
    string Url,
    string OutputDirectory,
    string TempDirectory,
    SingleDownloadKind Kind,
    AudioFormat AudioFormat,
    int? MaxHeight,
    DownloadOptions Options);

/// <param name="Files">Absolute paths of the saved files (usually one); empty on failure.</param>
public sealed record SingleDownloadResult(IReadOnlyList<string> Files, DownloadError? Error)
{
    public bool Succeeded => Error is null && Files.Count > 0;
}

/// <summary>
/// The toolbox's one-off downloader: a single URL into any folder, outside of any playlist and its manifest.
/// </summary>
public sealed class SingleDownloader(IProcessRunner runner, ToolLocator tools, FolderResolver folders, AppPaths paths,
    ILogger<SingleDownloader> log)
{
    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="directory"/> (local path or network address). Failures are
    /// returned; only cancellation throws (the yt-dlp process tree is killed, intermediates stay in the temp folder).
    /// </summary>
    public async Task<SingleDownloadResult> DownloadAsync(
        string url,
        string directory,
        SingleDownloadKind kind,
        AudioFormat audioFormat,
        int? maxHeight,
        AppSettings settings,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var ytDlp = tools.Locate(ToolKind.YtDlp, settings.Tools);
        if (ytDlp is not { Exists: true })
            return Fail(DownloadErrorKind.ToolMissing, "yt-dlp was not found. Install it or set its path in Settings → Tools.");
        var ffmpeg = tools.Locate(ToolKind.Ffmpeg, settings.Tools);
        if (ffmpeg is not { Exists: true })
            return Fail(DownloadErrorKind.ToolMissing, "ffmpeg was not found. Install it or set its path in Settings → Tools.");

        string target;
        try
        {
            target = (await folders.ResolveAsync(directory, ct).ConfigureAwait(false)).Path;
            Directory.CreateDirectory(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(DownloadErrorKind.Other, $"The folder '{directory}' can't be used: {ex.Message}");
        }

        var temp = Path.Combine(paths.DataDir, "toolbox-tmp");
        try
        {
            Directory.CreateDirectory(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(DownloadErrorKind.Other, $"Could not create the temp folder '{temp}': {ex.Message}");
        }

        var spec = new SingleDownloadSpec(url, target, temp, kind, audioFormat, maxHeight, settings.Download);
        var args = YtDlpArgsBuilder.SingleDownload(spec, ffmpeg, tools.Locate(ToolKind.JsRuntime, settings.Tools));

        var files = new List<string>();
        var throttle = new YtDlpTrackDownloader.ProgressThrottle(progress);
        throttle.Report(new DownloadProgress(DownloadPhase.Starting));

        var request = new ProcessRequest(ytDlp.Path, args)
        {
            Environment = YtDlpArgsBuilder.Environment,
            CaptureStdout = false,
            OnStdoutLine = line =>
            {
                if (DownloadOutputParser.ParseDownload(line) is { } p) throttle.Report(p);
                else if (DownloadOutputParser.ParseFile(line) is { } file) lock (files) files.Add(file);
                else log.LogDebug("yt-dlp [toolbox]: {Line}", line);
            },
            OnStderrLine = line =>
            {
                if (DownloadOutputParser.ParsePostProcess(line) is { } p) throttle.Report(p, force: true);
                else if (!line.StartsWith(DownloadOutputParser.PostProcessMarker, StringComparison.Ordinal))
                    log.LogDebug("yt-dlp [toolbox]: {Line}", line);
            },
        };

        log.LogInformation("Toolbox download ({Kind}) of {Url} into {Dir}", kind, url, target);
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(request, ct).ConfigureAwait(false);
        }
        catch (ProcessStartException ex)
        {
            return Fail(DownloadErrorKind.ToolMissing, ex.Message);
        }

        if (!result.Succeeded)
        {
            var error = YtDlpTrackDownloader.DescribeError(result.StandardError);
            log.LogWarning("Toolbox download of {Url} failed (exit {Exit}): {Error}", url, result.ExitCode, error.Message);
            return new SingleDownloadResult([], error);
        }

        var saved = files.Where(File.Exists).ToList();
        if (saved.Count == 0)
            return Fail(DownloadErrorKind.Other, "yt-dlp finished but did not report a saved file.");

        log.LogInformation("Toolbox download saved {Files} in {Elapsed:0.0}s", string.Join(", ", saved), result.Duration.TotalSeconds);
        return new SingleDownloadResult(saved, null);
    }

    private static SingleDownloadResult Fail(DownloadErrorKind kind, string message) =>
        new([], new DownloadError(kind, message));
}
