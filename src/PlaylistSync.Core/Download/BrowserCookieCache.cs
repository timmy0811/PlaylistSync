using System.Text;
using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Download;

/// <summary>
/// Reads browser cookies once per sync instead of once per yt-dlp process.
/// <para>
/// <c>--cookies-from-browser</c> is slow: yt-dlp decrypts every cookie in the profile (thousands for a daily browser,
/// in pure Python unless pycryptodomex is installed) and asks the keyring each time. Passed to every track download it
/// cost that time per track. This runs the extraction once, keeps only the YouTube / Google cookies (in memory, never
/// the full jar), and hands each yt-dlp process its own private cookies.txt copy, since yt-dlp writes the jar back
/// when it exits. A snapshot is reused for <see cref="MaxAge"/>, so "Sync all" extracts once too.
/// </para>
/// </summary>
public sealed class BrowserCookieCache
{
    /// <summary>How long one extraction is reused. Short, so signing in again in the browser is picked up soon.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    /// <summary>Playlist syncs only talk to YouTube; its sign-in cookies live on these domains.</summary>
    private static readonly string[] KeptDomains = ["youtube.com", "google.com", "youtube-nocookie.com", "googlevideo.com", "ytimg.com"];

    private const string Header = "# Netscape HTTP Cookie File";
    private const string HttpOnlyPrefix = "#HttpOnly_";

    private readonly IProcessRunner _runner;
    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly ILogger<BrowserCookieCache> _log;
    private readonly SemaphoreSlim _extracting = new(1, 1);
    private readonly Lock _gate = new();
    private (string Key, CookieSnapshot Snapshot, DateTimeOffset At)? _current;
    private bool _cleanedUp;

    /// <param name="directory">Private folder for the cookie files; created owner-only on Unix. See <see cref="DefaultDirectory"/>.</param>
    public BrowserCookieCache(IProcessRunner runner, string directory, TimeProvider clock, ILogger<BrowserCookieCache> log)
    {
        _runner = runner;
        _directory = directory;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// <c>$XDG_RUNTIME_DIR</c> on Linux (a per-user tmpfs, so cookies never reach the disk), else the app's data folder
    /// (<c>%LOCALAPPDATA%</c> on Windows).
    /// </summary>
    public static string DefaultDirectory(AppPaths paths, Func<string, string?> getEnv)
    {
        var runtime = getEnv("XDG_RUNTIME_DIR");
        if (!OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(runtime) && Path.IsPathRooted(runtime))
            return Path.Combine(runtime, AppPaths.AppDirName, "cookies");
        return Path.Combine(paths.DataDir, "cookies");
    }

    /// <summary>
    /// The browser cookies for <paramref name="options"/>, or null when they don't read cookies from a browser or the
    /// extraction failed. On null the caller keeps <c>--cookies-from-browser</c>, so yt-dlp reports the real problem.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled; the yt-dlp process is killed.</exception>
    public async Task<CookieSnapshot?> GetAsync(DownloadOptions options, ToolLocation ytDlp, CancellationToken ct)
    {
        if (options.CookieMode != CookieMode.Browser || YtDlpArgsBuilder.BrowserSpec(options) is not { } spec) return null;
        var key = ytDlp.Path + "\n" + spec;

        // One extraction at a time: syncs started together wait for the first one and reuse its result.
        await _extracting.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_current is { } c && c.Key == key && _clock.GetUtcNow() - c.At < MaxAge) return c.Snapshot;
                _current = null;
            }

            var snapshot = await ExtractAsync(spec, ytDlp, ct).ConfigureAwait(false);
            if (snapshot is not null)
            {
                lock (_gate) _current = (key, snapshot, _clock.GetUtcNow());
            }
            return snapshot;
        }
        finally
        {
            _extracting.Release();
        }
    }

    /// <summary>Forgets the cached cookies, e.g. after YouTube asked to sign in, so the next sync reads the browser again.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            if (_current is null) return;
            _current = null;
        }
        _log.LogInformation("Browser cookies will be read again on the next sync");
    }

    private async Task<CookieSnapshot?> ExtractAsync(string spec, ToolLocation ytDlp, CancellationToken ct)
    {
        try
        {
            PrepareDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not create the cookie folder {Dir}; every download reads the browser itself", _directory);
            return null;
        }

        var raw = Path.Combine(_directory, $"export-{Guid.NewGuid():N}.txt");
        var request = new ProcessRequest(ytDlp.Path, YtDlpArgsBuilder.CookieExport(spec, raw))
        {
            Environment = YtDlpArgsBuilder.Environment,
            OnStderrLine = line =>
            {
                if (!IsMissingUrlNoise(line)) _log.LogDebug("yt-dlp [cookies]: {Line}", line);
            },
        };

        _log.LogInformation("Reading cookies from {Browser}", spec);
        try
        {
            ProcessResult result;
            try
            {
                result = await _runner.RunAsync(request, ct).ConfigureAwait(false);
            }
            catch (ProcessStartException ex)
            {
                _log.LogWarning(ex, "Could not run yt-dlp to read the browser cookies");
                return null;
            }

            // The exit code is always non-zero (no URL, see CookieExport); the written file is what counts.
            if (!File.Exists(raw))
            {
                _log.LogWarning("Reading cookies from {Browser} failed; every download reads the browser itself: {Error}",
                    spec, YtDlpErrorText.LastError(WithoutMissingUrlNoise(result.StandardError)));
                return null;
            }

            var text = await File.ReadAllTextAsync(raw, CancellationToken.None).ConfigureAwait(false);
            var filtered = Filter(text, out var kept, out var total);
            _log.LogInformation("Read {Total} cookies from {Browser} in {Elapsed:0.0}s, kept {Kept} for YouTube",
                total, spec, result.Duration.TotalSeconds, kept);
            if (kept == 0) _log.LogWarning("{Browser} has no YouTube cookies. Is YouTube signed in in that browser profile?", spec);
            return new CookieSnapshot(filtered, _directory, _log);
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Could not read the exported cookies; every download reads the browser itself");
            return null;
        }
        finally
        {
            TryDelete(raw);
        }
    }

    /// <summary>
    /// Keeps the cookies of <see cref="KeptDomains"/> (and their subdomains) from a Netscape cookies.txt.
    /// </summary>
    internal static string Filter(string cookiesTxt, out int kept, out int total)
    {
        kept = 0;
        total = 0;
        var sb = new StringBuilder(Header).Append('\n');
        foreach (var rawLine in cookiesTxt.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var cookie = line.StartsWith(HttpOnlyPrefix, StringComparison.Ordinal) ? line[HttpOnlyPrefix.Length..] : line;
            if (cookie.Length == 0 || cookie[0] == '#') continue;

            var tab = cookie.IndexOf('\t');
            if (tab <= 0) continue;
            total++;
            var domain = cookie[..tab].TrimStart('.').ToLowerInvariant();
            if (!KeptDomains.Any(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal))) continue;

            sb.Append(line).Append('\n');
            kept++;
        }
        return sb.ToString();
    }

    private void PrepareDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_directory);
        }
        else
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(_directory, ownerOnly);
            File.SetUnixFileMode(_directory, ownerOnly); // in case it already existed with a wider mode
        }

        if (_cleanedUp) return;
        _cleanedUp = true;
        // Copies left behind by a crash or a killed app. Old enough that no running sync still uses them.
        var cutoff = _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(1);
        foreach (var file in Directory.EnumerateFiles(_directory, "*.txt"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff) TryDelete(file);
        }
    }

    private static bool IsMissingUrlNoise(string line) =>
        line.Contains("at least one URL", StringComparison.Ordinal)
        || line.StartsWith("Usage:", StringComparison.Ordinal)
        || line.StartsWith("Type yt-dlp --help", StringComparison.Ordinal);

    private static string WithoutMissingUrlNoise(string stderr) =>
        string.Join('\n', stderr.Split('\n').Where(l => !IsMissingUrlNoise(l.TrimEnd('\r'))));

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; PrepareDirectory removes leftovers later.
        }
    }
}

/// <summary>YouTube cookies read from a browser, held in memory.</summary>
public sealed class CookieSnapshot
{
    private readonly string _content;
    private readonly string _directory;
    private readonly ILogger _log;

    internal CookieSnapshot(string content, string directory, ILogger log)
    {
        _content = content;
        _directory = directory;
        _log = log;
    }

    /// <summary>
    /// Writes a private cookies.txt for one yt-dlp process (owner-only on Unix); dispose it when the process has exited.
    /// Null if it can't be written; the caller then falls back to reading the browser.
    /// </summary>
    public CookieFile? WriteCopy()
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.txt");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(path, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(_content);
            }
            return new CookieFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not write a cookie file into {Dir}", _directory);
            BrowserCookieCache.TryDelete(path);
            return null;
        }
    }
}

/// <summary>A cookies.txt for one yt-dlp process; deleted on dispose.</summary>
public sealed class CookieFile(string path) : IDisposable
{
    public string Path { get; } = path;

    /// <summary><paramref name="options"/> using this file instead of reading the browser.</summary>
    public DownloadOptions Apply(DownloadOptions options) =>
        options with { CookieMode = CookieMode.File, CookiesFile = Path, CookiesBrowser = null, CookiesBrowserProfile = null };

    public void Dispose() => BrowserCookieCache.TryDelete(Path);
}
