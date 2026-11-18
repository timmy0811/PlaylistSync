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

    /// <summary>
    /// Searches YouTube Music songs for <paramref name="query"/> and lists the first <paramref name="maxResults"/> hits as
    /// one JSON document (ids and titles only).
    /// </summary>
    public static IReadOnlyList<string> ReplacementSearch(string query, int maxResults, DownloadOptions options, ToolLocation? jsRuntime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var args = new List<string>
        {
            "--ignore-config",
            "--flat-playlist",
            "--dump-single-json",
            "--playlist-end", Math.Max(1, maxResults).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        AddCommon(args, options, jsRuntime);
        args.Add("--");
        // "#songs" selects the Songs shelf (yt-dlp's YouTube Music search section), so videos and podcasts don't crowd it.
        args.Add("https://music.youtube.com/search?q=" + Uri.EscapeDataString(query.Trim()) + "#songs");
        return args;
    }

    /// <summary>Full metadata of one video (artists, track title, duration) as JSON, without downloading it.</summary>
    public static IReadOnlyList<string> VideoProbe(string videoId, DownloadOptions options, ToolLocation? jsRuntime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);

        var args = new List<string>
        {
            "--ignore-config",
            "--no-playlist",
            "--dump-single-json",
            "--skip-download",
            "--no-warnings",
        };
        AddCommon(args, options, jsRuntime);
        args.Add("--");
        args.Add(Sources.YouTubeUrl.VideoUrl(videoId));
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
    /// Downloads one video into <paramref name="outputDirectory"/> (a per-track temp dir; the caller moves the result
    /// into the playlist folder): as an audio file for a music playlist, as a video file for a video playlist.
    /// Progress and the final path are printed with the markers <see cref="DownloadOutputParser"/> understands.
    /// </summary>
    /// <param name="trackNumber">1-based number for the filename prefix; only used with <see cref="DownloadOptions.TrackNumberPrefix"/>.</param>
    public static IReadOnlyList<string> TrackDownload(
        string videoId,
        string outputDirectory,
        DownloadOptions o,
        ToolLocation? ffmpeg,
        ToolLocation? jsRuntime,
        int? trackNumber = null,
        int trackNumberWidth = 2,
        PlaylistKind kind = PlaylistKind.Music) =>
        TrackDownload(videoId, outputDirectory, o, ffmpeg, jsRuntime, trackNumber, trackNumberWidth, OperatingSystem.IsWindows(),
            kind: kind);

    /// <param name="keepThumbnail">
    /// Also save the video thumbnail as <c>thumbnail.&lt;ext&gt;</c> in <paramref name="outputDirectory"/> (for the track
    /// list image; see <see cref="Covers.TrackThumbnails"/>). Only the audio file is moved out of that temp dir.
    /// </param>
    internal static IReadOnlyList<string> TrackDownload(
        string videoId,
        string outputDirectory,
        DownloadOptions o,
        ToolLocation? ffmpeg,
        ToolLocation? jsRuntime,
        int? trackNumber,
        int trackNumberWidth,
        bool isWindows,
        bool keepThumbnail = false,
        PlaylistKind kind = PlaylistKind.Music)
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
        };
        var video = kind == PlaylistKind.Video;

        if (video)
        {
            AddVideoFormat(args, o.VideoMaxHeight, o.VideoContainer);
        }
        else
        {
            args.Add("-f");
            args.Add("bestaudio/best");
            args.Add("-x");
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
        }

        if (ffmpeg is { Exists: true })
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpeg.Path);
        }

        // Metadata. Splitting "Artist - Title", the square cover and SponsorBlock's music category are for music only.
        if (o.ParseArtistTitle && !video)
        {
            args.Add("--parse-metadata");
            args.Add(ParseArtistTitleArgs);
        }
        args.Add(o.EmbedMetadata ? "--embed-metadata" : "--no-embed-metadata");
        // WAV can't carry a cover; yt-dlp would fail the whole download.
        if (o.EmbedThumbnail && (video || o.Format != AudioFormat.Wav))
        {
            args.Add("--embed-thumbnail");
            if (o.CropThumbnailSquare && !video)
            {
                args.Add("--convert-thumbnails");
                args.Add("jpg");
                args.Add("--ppa");
                args.Add(CropThumbnailArgs);
            }
        }
        args.Add(o.EmbedChapters ? "--embed-chapters" : "--no-embed-chapters");
        if (video && o.EmbedSubtitles)
        {
            args.Add("--embed-subs");
            args.Add("--sub-langs");
            args.Add(string.IsNullOrWhiteSpace(o.SubtitleLanguages) ? DownloadOptions.DefaultSubtitleLanguages : o.SubtitleLanguages.Trim());
        }
        if (o.SponsorBlockRemoveOfftopic && !video)
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
        args.Add(OutputTemplate(o, o.TrackNumberPrefix ? trackNumber : null, trackNumberWidth, kind));
        if (keepThumbnail)
        {
            // With --embed-thumbnail too, yt-dlp keeps the (converted, cropped) file instead of deleting it after embedding.
            args.Add("--write-thumbnail");
            args.Add("-o");
            args.Add("thumbnail:" + Covers.TrackThumbnails.TempFileStem + ".%(ext)s");
        }

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

    /// <summary>Filename template for toolbox video downloads.</summary>
    public const string SingleVideoFilenameTemplate = "%(title)s.%(ext)s";

    /// <summary>
    /// Toolbox download: one URL (any site yt-dlp supports) straight into <see cref="SingleDownloadSpec.OutputDirectory"/>,
    /// as audio (format/quality/tagging from the global options) or as video. Intermediate files go to
    /// <see cref="SingleDownloadSpec.TempDirectory"/>, so only finished files appear in the target folder.
    /// </summary>
    public static IReadOnlyList<string> SingleDownload(SingleDownloadSpec spec, ToolLocation? ffmpeg, ToolLocation? jsRuntime) =>
        SingleDownload(spec, ffmpeg, jsRuntime, OperatingSystem.IsWindows());

    internal static IReadOnlyList<string> SingleDownload(SingleDownloadSpec spec, ToolLocation? ffmpeg, ToolLocation? jsRuntime, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Url);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.OutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.TempDirectory);
        var o = spec.Options;

        var args = new List<string>
        {
            "--ignore-config",
            "--no-playlist",
            "--newline",
            "--progress",
            "--progress-template", "download:" + DownloadOutputParser.DownloadMarker + "%(progress)j",
            "--progress-template", "postprocess:" + DownloadOutputParser.PostProcessMarker + "%(progress.status)s %(progress.postprocessor)s",
            "--print", "after_move:" + DownloadOutputParser.FileMarker + "%(filepath)s",
            "--no-mtime",
        };

        string template;
        bool canEmbedThumbnail;
        if (spec.Kind == SingleDownloadKind.Audio)
        {
            args.Add("-f");
            args.Add("bestaudio/best");
            args.Add("-x");
            if (spec.AudioFormat != AudioFormat.Best)
            {
                args.Add("--audio-format");
                args.Add(FormatName(spec.AudioFormat));
                if (spec.AudioFormat is not (AudioFormat.Flac or AudioFormat.Wav))
                {
                    args.Add("--audio-quality");
                    args.Add(o.QualityMode == QualityMode.Cbr
                        ? $"{Math.Clamp(o.CbrKbps, 32, 320)}K"
                        : Math.Clamp(o.VbrLevel, 0, 10).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            if (o.ParseArtistTitle)
            {
                args.Add("--parse-metadata");
                args.Add(ParseArtistTitleArgs);
            }
            template = string.IsNullOrWhiteSpace(o.FilenameTemplate) ? DownloadOptions.DefaultFilenameTemplate : o.FilenameTemplate.Trim();
            canEmbedThumbnail = spec.AudioFormat != AudioFormat.Wav;
        }
        else
        {
            AddVideoFormat(args, spec.MaxHeight, VideoContainer.Mp4);
            template = SingleVideoFilenameTemplate;
            canEmbedThumbnail = true;
        }

        if (ffmpeg is { Exists: true })
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpeg.Path);
        }

        args.Add(o.EmbedMetadata ? "--embed-metadata" : "--no-embed-metadata");
        if (o.EmbedThumbnail && canEmbedThumbnail)
        {
            args.Add("--embed-thumbnail");
            if (spec.Kind == SingleDownloadKind.Audio && o.CropThumbnailSquare)
            {
                args.Add("--convert-thumbnails");
                args.Add("jpg");
                args.Add("--ppa");
                args.Add(CropThumbnailArgs);
            }
        }
        args.Add(o.EmbedChapters || spec.Kind == SingleDownloadKind.Video ? "--embed-chapters" : "--no-embed-chapters");

        if (o.RestrictFilenames) args.Add("--restrict-filenames");
        if (isWindows) args.Add("--windows-filenames");
        args.Add("-P");
        args.Add(spec.OutputDirectory);
        args.Add("-P");
        args.Add("temp:" + spec.TempDirectory);
        args.Add("-o");
        args.Add(template);

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

        AddCommon(args, o, jsRuntime);

        args.Add("--");
        args.Add(spec.Url.Trim());
        return args;
    }

    /// <summary>
    /// Best video + best audio, merged. Sort, not filter: prefers the highest resolution up to
    /// <paramref name="maxHeight"/> (falls back to the closest if none fits). For MP4 also MP4/M4A streams, so they can be
    /// merged without re-encoding; MKV falls back from MP4 when the streams don't fit.
    /// </summary>
    internal static void AddVideoFormat(List<string> args, int? maxHeight, VideoContainer container)
    {
        var sort = new List<string>();
        if (maxHeight is { } h and > 0) sort.Add("res:" + h.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (container == VideoContainer.Mp4) sort.Add("ext:mp4:m4a");

        args.Add("-f");
        args.Add("bv*+ba/b");
        if (sort.Count > 0)
        {
            args.Add("-S");
            args.Add(string.Join(',', sort));
        }
        args.Add("--merge-output-format");
        args.Add(container == VideoContainer.Mkv ? "mkv" : "mp4/mkv");
    }

    /// <summary>The filename template, with the track number put in front of the file name (not of a folder in it).</summary>
    internal static string OutputTemplate(DownloadOptions o, int? trackNumber, int width, PlaylistKind kind = PlaylistKind.Music)
    {
        var template = o.TemplateFor(kind);
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

    /// <summary>The <c>--cookies-from-browser</c> value (<c>browser</c> or <c>browser:profile</c>); null if no browser is set.</summary>
    public static string? BrowserSpec(DownloadOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.CookiesBrowser)) return null;
        return string.IsNullOrWhiteSpace(o.CookiesBrowserProfile)
            ? o.CookiesBrowser.Trim()
            : $"{o.CookiesBrowser.Trim()}:{o.CookiesBrowserProfile.Trim()}";
    }

    /// <summary>
    /// Copies a browser's cookies into the cookies.txt <paramref name="file"/> (see <see cref="BrowserCookieCache"/>).
    /// No URL on purpose: yt-dlp then stops with "You must provide at least one URL" (exit code 2), but on the way out
    /// it still loads the browser cookies and saves them to <c>--cookies</c>. Judge success by the file, not the exit code.
    /// </summary>
    public static IReadOnlyList<string> CookieExport(string browserSpec, string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browserSpec);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        return ["--ignore-config", "--cookies-from-browser", browserSpec, "--cookies", file];
    }

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
            case CookieMode.Browser when BrowserSpec(o) is { } spec:
                args.Add("--cookies-from-browser");
                args.Add(spec);
                break;
        }

        if (!string.IsNullOrWhiteSpace(o.Proxy))
        {
            args.Add("--proxy");
            args.Add(o.Proxy.Trim());
        }
    }
}
