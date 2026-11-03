using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Sources;

/// <summary>YouTube / YouTube Music playlists via <c>yt-dlp --flat-playlist</c>.</summary>
public sealed class YtDlpPlaylistSource(ToolLocator locator, IProcessRunner runner, ILogger<YtDlpPlaylistSource> log)
    : IPlaylistSource
{
    public bool CanHandle(string url) => YouTubeUrl.IsYouTube(url);

    public async Task<RemotePlaylist> FetchAsync(string url, SourceContext context, CancellationToken ct = default)
    {
        if (!CanHandle(url))
            throw new PlaylistFetchException(FetchErrorKind.NotAPlaylist, "Only YouTube and YouTube Music playlist URLs are supported.");

        var ytDlp = locator.Locate(ToolKind.YtDlp, context.Tools);
        if (ytDlp is not { Exists: true })
        {
            throw new PlaylistFetchException(FetchErrorKind.ToolMissing, ytDlp is null
                ? "yt-dlp was not found. Install it or let the app download it in Settings → Tools."
                : $"The configured yt-dlp path '{ytDlp.Path}' does not exist.");
        }
        var js = locator.Locate(ToolKind.JsRuntime, context.Tools);

        var request = new ProcessRequest(ytDlp.Path, YtDlpArgsBuilder.PlaylistFetch(url, context.Options, js))
        {
            Environment = YtDlpArgsBuilder.Environment,
            OnStderrLine = line => log.LogDebug("yt-dlp: {Line}", line),
        };

        log.LogInformation("Fetching playlist {Url}", url);
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(request, ct).ConfigureAwait(false);
        }
        catch (ProcessStartException ex)
        {
            throw new PlaylistFetchException(FetchErrorKind.ToolMissing, ex.Message, ex);
        }

        if (!result.Succeeded)
        {
            var error = DescribeError(result.StandardError, url);
            log.LogWarning("Fetching {Url} failed (exit {Exit}): {Error}", url, result.ExitCode, error.Message);
            throw error;
        }

        var playlist = FlatPlaylistParser.Parse(result.StandardOutput);
        log.LogInformation("Fetched '{Title}': {Count} tracks ({Unavailable} unavailable) in {Elapsed:0.0}s",
            playlist.Title, playlist.Tracks.Count, playlist.Tracks.Count(t => !t.IsAvailable), result.Duration.TotalSeconds);
        return playlist;
    }

    /// <summary>Turns yt-dlp's stderr into a user-facing error, with a hint where we recognise the cause.</summary>
    internal static PlaylistFetchException DescribeError(string stderr, string? url = null)
    {
        var message = YtDlpErrorText.LastError(stderr);

        // Watch Later / Liked videos / Liked music only exist for a signed-in user; without one YouTube says "does not exist".
        if (YouTubeUrl.PlaylistId(url) is "WL" or "LL" or "LM" || YtDlpErrorText.NeedsCookies(message))
            return new PlaylistFetchException(FetchErrorKind.NeedsCookies, $"{message}\n{YtDlpErrorText.CookiesHint}");
        if (YtDlpErrorText.ContainsAny(message, "Unsupported URL"))
            return new PlaylistFetchException(FetchErrorKind.NotAPlaylist, message);
        return new PlaylistFetchException(FetchErrorKind.Other, message);
    }
}
