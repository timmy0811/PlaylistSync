using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Download;

/// <summary>
/// The only place yt-dlp command lines are built. Returns argument lists for
/// <c>ProcessStartInfo.ArgumentList</c>; nothing here is ever joined into a shell string.
/// </summary>
public static class YtDlpArgsBuilder
{
    /// <summary>
    /// Environment for every yt-dlp run: forces UTF-8 output so titles survive on Windows consoles.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> Environment = new Dictionary<string, string?>
    {
        ["PYTHONIOENCODING"] = "utf-8",
        ["PYTHONUTF8"] = "1",
    };

    /// <summary>
    /// Lists a playlist's entries as a single JSON document on stdout, without resolving each video.
    /// </summary>
    public static IReadOnlyList<string> PlaylistFetch(string url, DownloadOptions options, ToolLocation? jsRuntime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var args = new List<string>
        {
            // The app owns all options; a user's yt-dlp config (e.g. -o, -x) must not change behaviour.
            "--ignore-config",
            "--flat-playlist",
            "--dump-single-json",
            "--yes-playlist",
        };
        AddCommon(args, options, jsRuntime);
        // "--" so a URL can never be read as an option.
        args.Add("--");
        args.Add(url.Trim());
        return args;
    }

    /// <summary>Square-crops the cover (YouTube thumbnails are 16:9) while converting it to JPEG.</summary>
    internal const string CropThumbnailArgs =
        "ThumbnailsConvertor+FFmpeg_o:-c:v mjpeg -vf crop=\"'if(gt(ih,iw),iw,ih)':'if(gt(iw,ih),ih,iw)'\"";

    /// <summary>
    /// Splits "Artist - Title" into artist/title tags, but only when YouTube didn't supply an artist
    /// (YouTube Music tracks have one, and their titles may legitimately contain " - ").
    /// </summary>
    internal const string ParseArtistTitleArgs =
        "%(artist&ARTIST-KNOWN|)s%(title)s:^(?!ARTIST-KNOWN)(?P<artist>.+?) - (?P<title>.+)$";

    /// <summary>
    /// Downloads one video as an audio file into <paramref name="outputDirectory"/> (a per-track temp dir;
    /// the caller moves the result into the playlist folder). Progress and the final path are printed with the
    /// markers <see cref="DownloadOutputParser"/> understands.
    /// </summary>
    /// <param name="trackNumber">1-based number for the filename prefix; only used with <see cref="DownloadOptions.TrackNumberPrefix"/>.</param>
    public static IReadOnlyList<string> TrackDownload(
        string videoId,
        string outputDirectory,
        DownloadOptions o,
        ToolLocation? ffmpeg,
        ToolLocation? jsRuntime,
        int? trackNumber = null,
        int trackNumberWidth = 2) =>
        TrackDownload(videoId, outputDirectory, o, ffmpeg, jsRuntime, trackNumber, trackNumberWidth, OperatingSystem.IsWindows());

    internal static IReadOnlyList<string> TrackDownload(
        string videoId,
        string outputDirectory,
        DownloadOptions o,
        ToolLocation? ffmpeg,
        ToolLocation? jsRuntime,
        int? trackNumber,
        int trackNumberWidth,
        bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var args = new List<string>
        {
            "--ignore-config",
            "--no-playlist",
            // --print implies --quiet; --progress keeps the (machine-readable) progress lines coming.
            "--newline",
            "--progress",
            "--progress-template", "download:" + DownloadOutputParser.DownloadMarker + "%(progress)j",
            "--progress-template", "postprocess:" + DownloadOutputParser.PostProcessMarker + "%(progress.status)s %(progress.postprocessor)s",
            "--print", "after_move:" + DownloadOutputParser.FileMarker + "%(filepath)s",
            // Players sort by date added; the upload date as mtime would scatter new tracks.
            "--no-mtime",
            "-f", "bestaudio/best",
            "-x",
        };

        if (o.Format != AudioFormat.Best)
        {
            args.Add("--audio-format");
            args.Add(FormatName(o.Format));
            if (o.Format is not (AudioFormat.Flac or AudioFormat.Wav))
            {
                args.Add("--audio-quality");
                args.Add(o.QualityMode == QualityMode.Cbr
                    ? $"{Math.Clamp(o.CbrKbps, 32, 320)}K"
                    : Math.Clamp(o.VbrLevel, 0, 10).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        if (ffmpeg is { Exists: true })
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpeg.Path);
        }

        // Metadata
        if (o.ParseArtistTitle)
        {
            args.Add("--parse-metadata");
            args.Add(ParseArtistTitleArgs);
        }
        args.Add(o.EmbedMetadata ? "--embed-metadata" : "--no-embed-metadata");
        // WAV can't carry a cover; yt-dlp would fail the whole download.
        if (o.EmbedThumbnail && o.Format != AudioFormat.Wav)
        {
            args.Add("--embed-thumbnail");
            if (o.CropThumbnailSquare)
            {
                args.Add("--convert-thumbnails");
                args.Add("jpg");
                args.Add("--ppa");
                args.Add(CropThumbnailArgs);
            }
        }
        args.Add(o.EmbedChapters ? "--embed-chapters" : "--no-embed-chapters");
        if (o.SponsorBlockRemoveOfftopic)
        {
            args.Add("--sponsorblock-remove");
            args.Add("music_offtopic");
        }

        // Files
        if (o.RestrictFilenames) args.Add("--restrict-filenames");
        if (isWindows) args.Add("--windows-filenames");
        args.Add("-P");
        args.Add(outputDirectory);
        args.Add("-o");
        args.Add(OutputTemplate(o, o.TrackNumberPrefix ? trackNumber : null, trackNumberWidth));

        // Network
        var retries = Math.Max(0, o.Retries).ToString(System.Globalization.CultureInfo.InvariantCulture);
        args.Add("--retries");
        args.Add(retries);
        args.Add("--fragment-retries");
        args.Add(retries);
        if (!string.IsNullOrWhiteSpace(o.RateLimit))
        {
            args.Add("--limit-rate");
            args.Add(o.RateLimit.Trim());
        }
        if (o.SleepIntervalSeconds > 0)
        {
            args.Add("--sleep-interval");
            args.Add(o.SleepIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        AddCommon(args, o, jsRuntime);

        // Last before "--", so advanced users can override any of the above.
        if (!string.IsNullOrWhiteSpace(o.ExtraArgs)) args.AddRange(CommandLineSplitter.Split(o.ExtraArgs));

        args.Add("--");
        args.Add(Sources.YouTubeUrl.VideoUrl(videoId));
        return args;
    }

    /// <summary>The filename template, with the track number put in front of the file name (not of a folder in it).</summary>
    internal static string OutputTemplate(DownloadOptions o, int? trackNumber, int width)
    {
        var template = string.IsNullOrWhiteSpace(o.FilenameTemplate) ? DownloadOptions.DefaultFilenameTemplate : o.FilenameTemplate.Trim();
        if (trackNumber is not { } n) return template;

        var prefix = n.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(Math.Max(1, width), '0')
                     + (o.RestrictFilenames ? "_-_" : " - ");
        var cut = Math.Max(template.LastIndexOf('/'), template.LastIndexOf('\\')) + 1;
        return template.Insert(cut, prefix);
    }

    internal static string FormatName(AudioFormat format) => format switch
    {
        AudioFormat.Mp3 => "mp3",
        AudioFormat.M4a => "m4a",
        AudioFormat.Opus => "opus",
        AudioFormat.Flac => "flac",
        AudioFormat.Ogg => "vorbis",
        AudioFormat.Wav => "wav",
        _ => "best",
    };

    /// <summary>Options shared by fetch and download: JS runtime, cookies, network.</summary>
    internal static void AddCommon(List<string> args, DownloadOptions o, ToolLocation? jsRuntime)
    {
        if (jsRuntime is { Exists: true })
        {
            // yt-dlp only enables deno by default; node/bun must be named. Passing the path also
            // covers runtimes that are not on PATH (configured or app-managed).
            args.Add("--js-runtimes");
            args.Add($"{jsRuntime.Name}:{jsRuntime.Path}");
        }

        switch (o.CookieMode)
        {
            case CookieMode.File when !string.IsNullOrWhiteSpace(o.CookiesFile):
                args.Add("--cookies");
                args.Add(o.CookiesFile.Trim());
                break;
            case CookieMode.Browser when !string.IsNullOrWhiteSpace(o.CookiesBrowser):
                args.Add("--cookies-from-browser");
                args.Add(string.IsNullOrWhiteSpace(o.CookiesBrowserProfile)
                    ? o.CookiesBrowser.Trim()
                    : $"{o.CookiesBrowser.Trim()}:{o.CookiesBrowserProfile.Trim()}");
                break;
        }

        if (!string.IsNullOrWhiteSpace(o.Proxy))
        {
            args.Add("--proxy");
            args.Add(o.Proxy.Trim());
        }
    }
}
