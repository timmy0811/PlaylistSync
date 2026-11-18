using System.Text.RegularExpressions;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playlists;

namespace PlaylistSync.Core.Settings;

/// <summary>Field errors (block saving) and warnings (shown, but saving is allowed) for the settings dialog.</summary>
public sealed record SettingsValidation(IReadOnlyDictionary<string, string> Errors, IReadOnlyDictionary<string, string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
    public string? Error(string field) => Errors.GetValueOrDefault(field);
    public string? Warning(string field) => Warnings.GetValueOrDefault(field);
}

/// <summary>
/// Pure checks of user-entered settings. Keys are the property names of <see cref="DownloadOptions"/>,
/// <see cref="AppSettings"/> and <see cref="ToolSettings"/>.
/// </summary>
public static partial class SettingsValidator
{
    public const int MaxConcurrency = 8;
    public const int MaxRetries = 100;
    public const int MaxSleepSeconds = 3600;

    /// <summary>Browsers yt-dlp can read cookies from (<c>--cookies-from-browser</c>).</summary>
    public static readonly IReadOnlyList<string> CookieBrowsers =
        ["firefox", "chrome", "chromium", "brave", "edge", "opera", "vivaldi", "whale", "safari"];

    /// <summary>
    /// Extra arguments that break how the app drives yt-dlp (output location, machine-readable output, input list).
    /// Allowed, but warned about.
    /// </summary>
    private static readonly HashSet<string> InterferingArgs = new(StringComparer.Ordinal)
    {
        "-o", "--output", "-P", "--paths", "-O", "--print", "--print-to-file", "-q", "--quiet", "--no-progress",
        "--progress-template", "-a", "--batch-file", "--config-location", "--config-locations", "--exec",
        "-s", "--simulate", "--skip-download", "--yes-playlist", "-j", "--dump-json", "-J", "--dump-single-json",
    };

    /// <param name="fileExists">Used for the cookies file; injectable for tests.</param>
    /// <param name="playlistScope">
    /// Validating a playlist's own options: the network group is not shown there (it always comes from the global settings),
    /// so it isn't checked either.
    /// </param>
    public static SettingsValidation ValidateDownload(DownloadOptions o, Func<string, bool> fileExists, bool playlistScope = false)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new Dictionary<string, string>(StringComparer.Ordinal);

        if (o.VbrLevel is < 0 or > 10)
            errors[nameof(o.VbrLevel)] = "Choose a level from 0 (best) to 10.";
        if (o.CbrKbps is < 32 or > 320)
            errors[nameof(o.CbrKbps)] = "Choose a bitrate from 32 to 320 kbps.";

        if (TemplateError(o.FilenameTemplate) is { } templateError)
            errors[nameof(o.FilenameTemplate)] = templateError;
        if (TemplateError(o.VideoFilenameTemplate) is { } videoTemplateError)
            errors[nameof(o.VideoFilenameTemplate)] = videoTemplateError;
        if (o.VideoMaxHeight is < 144 or > 4320)
            errors[nameof(o.VideoMaxHeight)] = "Choose a resolution from 144p to 4320p, or Best.";
        if (o.EmbedSubtitles && !SubLangsRegex().IsMatch(o.SubtitleLanguages?.Trim() ?? ""))
            errors[nameof(o.SubtitleLanguages)] = "List language codes separated by commas, e.g. en.*,de (or 'all').";

        if (o.EmbedThumbnail && o.Format == AudioFormat.Wav)
            warnings[nameof(o.EmbedThumbnail)] = "WAV files can't hold a cover, so none is embedded.";

        var args = CommandLineSplitter.Split(o.ExtraArgs, out var unclosed);
        if (unclosed)
        {
            errors[nameof(o.ExtraArgs)] = "A quote is not closed.";
        }
        else if (args.FirstOrDefault(a => InterferingArgs.Contains(OptionName(a))) is { } bad)
        {
            warnings[nameof(o.ExtraArgs)] =
                $"'{OptionName(bad)}' changes how Playlist Sync talks to yt-dlp; downloads may fail or end up in the wrong place.";
        }

        if (!playlistScope)
        {
            switch (o.CookieMode)
            {
                case CookieMode.File when string.IsNullOrWhiteSpace(o.CookiesFile):
                    errors[nameof(o.CookiesFile)] = "Choose a cookies.txt file.";
                    break;
                case CookieMode.File when !fileExists(o.CookiesFile!.Trim()):
                    errors[nameof(o.CookiesFile)] = "This file doesn't exist.";
                    break;
                case CookieMode.Browser when string.IsNullOrWhiteSpace(o.CookiesBrowser):
                    errors[nameof(o.CookiesBrowser)] = "Choose a browser.";
                    break;
                case CookieMode.Browser when !CookieBrowsers.Contains(o.CookiesBrowser!.Trim().ToLowerInvariant()):
                    errors[nameof(o.CookiesBrowser)] = "yt-dlp can't read cookies from this browser.";
                    break;
            }

            if (!string.IsNullOrWhiteSpace(o.Proxy) && !ProxyRegex().IsMatch(o.Proxy.Trim()))
                errors[nameof(o.Proxy)] = "Use a URL like socks5://127.0.0.1:1080 or http://host:3128.";
            if (!string.IsNullOrWhiteSpace(o.RateLimit) && !RateRegex().IsMatch(o.RateLimit.Trim()))
                errors[nameof(o.RateLimit)] = "Use bytes per second with an optional unit, e.g. 500K or 2M.";
            if (o.SleepIntervalSeconds is < 0 or > MaxSleepSeconds)
                errors[nameof(o.SleepIntervalSeconds)] = $"Use 0 to {MaxSleepSeconds} seconds.";
            if (o.Retries is < 0 or > MaxRetries)
                errors[nameof(o.Retries)] = $"Use 0 to {MaxRetries}.";
        }

        return new SettingsValidation(errors, warnings);
    }

    /// <summary>Everything outside the download options: concurrency and tool paths.</summary>
    /// <param name="home">Home folder used to expand <c>~</c>; defaults to the user profile.</param>
    public static SettingsValidation ValidateApp(AppSettings s, string? home = null)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (s.Concurrency is < 1 or > MaxConcurrency)
            errors[nameof(s.Concurrency)] = $"Use 1 to {MaxConcurrency}.";
        if (s.VideoConcurrency is < 1 or > MaxConcurrency)
            errors[nameof(s.VideoConcurrency)] = $"Use 1 to {MaxConcurrency}.";
        if (!string.IsNullOrWhiteSpace(s.DefaultDirectory) &&
            PathUtil.NormalizeUserPath(s.DefaultDirectory, home ?? PathUtil.Home) is null)
            errors[nameof(s.DefaultDirectory)] = "Use a full path (~/… and smb://server/share/… work too), or leave it empty for the Music folder.";
        if (!string.IsNullOrWhiteSpace(s.DefaultVideoDirectory) &&
            PathUtil.NormalizeUserPath(s.DefaultVideoDirectory, home ?? PathUtil.Home) is null)
            errors[nameof(s.DefaultVideoDirectory)] = "Use a full path (~/… and smb://server/share/… work too), or leave it empty for the Videos folder.";
        Tool(nameof(ToolSettings.YtDlpPath), s.Tools.YtDlpPath);
        Tool(nameof(ToolSettings.FfmpegPath), s.Tools.FfmpegPath);
        Tool(nameof(ToolSettings.JsRuntimePath), s.Tools.JsRuntimePath);
        if (string.IsNullOrWhiteSpace(s.Playback.DesktopId) && !string.IsNullOrWhiteSpace(s.Playback.ExecutablePath) &&
            !Path.IsPathFullyQualified(s.Playback.ExecutablePath.Trim()))
            errors[nameof(PlaybackSettings.ExecutablePath)] = "Use the full path of the player program.";
        return new SettingsValidation(errors, new Dictionary<string, string>());

        void Tool(string key, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !Path.IsPathFullyQualified(path.Trim()))
                errors[key] = "Use a full path, or leave it empty to detect the tool automatically.";
        }
    }

    /// <summary>
    /// The template must stay inside the playlist folder and keep the extension: no absolute path, no "..", ends with
    /// <c>.%(ext)s</c>, balanced <c>%( … )</c> fields. Empty means the default.
    /// </summary>
    internal static string? TemplateError(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        var t = template.Trim();
        if (Path.IsPathRooted(t) || t.StartsWith('~'))
            return "Use a file name (optionally with sub-folders) inside the playlist folder, not a full path.";
        if (t.Split('/', '\\').Any(part => part == ".."))
            return "'..' would put files outside the playlist folder.";
        if (!t.EndsWith(".%(ext)s", StringComparison.Ordinal))
            return "End the template with .%(ext)s so files get the right extension.";

        var depth = 0;
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] == '%' && i + 1 < t.Length && t[i + 1] == '%') { i++; continue; }
            if (t[i] == '%' && i + 1 < t.Length && t[i + 1] == '(') { if (depth > 0) return BadField(); depth = 1; i++; continue; }
            if (t[i] == ')' && depth > 0) depth = 0;
        }
        return depth == 0 ? null : BadField();

        static string BadField() => "A %( … ) field is not closed.";
    }

    /// <summary>"--output=x" → "--output"; short options are matched as written.</summary>
    private static string OptionName(string arg)
    {
        if (!arg.StartsWith("--", StringComparison.Ordinal)) return arg;
        var eq = arg.IndexOf('=', StringComparison.Ordinal);
        return eq < 0 ? arg : arg[..eq];
    }

    [GeneratedRegex(@"^(https?|socks4a?|socks5h?)://\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex ProxyRegex();

    [GeneratedRegex(@"^\d+(\.\d+)?\s*([kmgt]i?)?b?$", RegexOptions.IgnoreCase)]
    private static partial Regex RateRegex();

    /// <summary>yt-dlp <c>--sub-langs</c>: comma-separated codes or regexes (<c>en.*</c>), optionally negated (<c>-live_chat</c>).</summary>
    [GeneratedRegex(@"^-?[A-Za-z0-9_.*+\-]+(,-?[A-Za-z0-9_.*+\-]+)*$")]
    private static partial Regex SubLangsRegex();
}
