using PlaylistSync.Core.Models;

namespace PlaylistSync.Core.Tools;

public enum ToolKind
{
    YtDlp,
    Ffmpeg,
    Ffprobe,
    /// <summary>JavaScript runtime yt-dlp uses for YouTube challenges (deno preferred, node/bun also work).</summary>
    JsRuntime,
}

public enum ToolSource
{
    /// <summary>Explicit path from <see cref="ToolSettings"/>.</summary>
    Configured,
    /// <summary>Downloaded by the app into <see cref="AppPaths.BinDir"/>; the app may update it.</summary>
    Managed,
    /// <summary>Found on PATH (e.g. installed by the package manager); the app never updates it.</summary>
    SystemPath,
}

/// <param name="Path">Full path of the executable (for a missing configured tool: the configured path).</param>
/// <param name="Name">Executable base name without extension, e.g. <c>deno</c> or <c>node</c>.</param>
/// <param name="Exists">False only when a configured path points to nothing.</param>
public sealed record ToolLocation(ToolKind Kind, string Path, string Name, ToolSource Source, bool Exists = true);

/// <summary>
/// Finds the external tools. Order per tool: configured path → app-managed bin dir → PATH.
/// A configured path that doesn't exist is reported as such instead of silently falling back,
/// so the user sees that their setting is wrong.
/// </summary>
public sealed class ToolLocator
{
    private readonly string _binDir;
    private readonly bool _isWindows;
    private readonly Func<string, string?> _getEnv;
    private readonly Func<string, bool> _isExecutableFile;

    public ToolLocator(AppPaths paths)
        : this(paths.BinDir, OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable, IsExecutableFile)
    {
    }

    internal ToolLocator(string binDir, bool isWindows, Func<string, string?> getEnv, Func<string, bool> isExecutableFile)
    {
        _binDir = binDir;
        _isWindows = isWindows;
        _getEnv = getEnv;
        _isExecutableFile = isExecutableFile;
    }

    /// <summary>Executable names to look for, in order of preference.</summary>
    public static IReadOnlyList<string> CandidateNames(ToolKind kind) => kind switch
    {
        ToolKind.YtDlp => ["yt-dlp"],
        ToolKind.Ffmpeg => ["ffmpeg"],
        ToolKind.Ffprobe => ["ffprobe"],
        ToolKind.JsRuntime => ["deno", "node", "bun"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public ToolLocation? Locate(ToolKind kind, ToolSettings settings)
    {
        var configured = ConfiguredPath(kind, settings);
        // ffprobe has no setting of its own: it is looked for next to a configured ffmpeg,
        // and falls back to the normal search if it isn't there.
        if (configured is not null && (kind != ToolKind.Ffprobe || _isExecutableFile(configured)))
        {
            return new ToolLocation(kind, configured, NameOf(configured), ToolSource.Configured,
                _isExecutableFile(configured));
        }

        foreach (var name in CandidateNames(kind))
        {
            var managed = Path.Combine(_binDir, _isWindows ? name + ".exe" : name);
            if (_isExecutableFile(managed))
                return new ToolLocation(kind, managed, name, ToolSource.Managed);
        }

        foreach (var name in CandidateNames(kind))
        {
            var found = FindOnPath(name);
            if (found is not null)
                return new ToolLocation(kind, found, name, ToolSource.SystemPath);
        }

        return null;
    }

    private string? ConfiguredPath(ToolKind kind, ToolSettings s)
    {
        var path = kind switch
        {
            ToolKind.YtDlp => s.YtDlpPath,
            ToolKind.Ffmpeg => s.FfmpegPath,
            ToolKind.JsRuntime => s.JsRuntimePath,
            ToolKind.Ffprobe => SiblingOf(s.FfmpegPath, "ffprobe"),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    private string? SiblingOf(string? ffmpegPath, string name)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath)) return null;
        var dir = Path.GetDirectoryName(ffmpegPath.Trim());
        if (string.IsNullOrEmpty(dir)) return null;
        return Path.Combine(dir, _isWindows ? name + ".exe" : name);
    }

    private string? FindOnPath(string name)
    {
        var pathVar = _getEnv("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        // Only real executables on Windows: .cmd/.bat shims can't be started without a shell.
        var fileNames = _isWindows ? new[] { name + ".exe" } : [name];
        var separator = _isWindows ? ';' : ':';

        foreach (var rawDir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim().Trim('"');
            if (dir.Length == 0 || !Path.IsPathRooted(dir)) continue;
            foreach (var file in fileNames)
            {
                var candidate = Path.Combine(dir, file);
                if (_isExecutableFile(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string NameOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        try
        {
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
