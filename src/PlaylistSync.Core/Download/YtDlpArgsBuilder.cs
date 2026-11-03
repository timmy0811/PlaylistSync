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
