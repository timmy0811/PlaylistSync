namespace PlaylistSync.Core.Sync;

/// <summary>
/// The files in a playlist directory at one point in time, as relative paths with <c>/</c> separators.
/// Comparison follows the platform: case-sensitive on Linux, case-insensitive on Windows.
/// </summary>
public sealed class DiskSnapshot
{
    private readonly HashSet<string> _files;

    public DiskSnapshot(IEnumerable<string> relativePaths, StringComparer comparer)
    {
        Comparer = comparer;
        _files = new HashSet<string>(relativePaths.Select(Normalize), comparer);
    }

    public static StringComparer PlatformComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static DiskSnapshot Empty { get; } = new([], PlatformComparer);

    public StringComparer Comparer { get; }

    public IReadOnlyCollection<string> Files => _files;

    public bool Contains(string relativePath) => _files.Contains(Normalize(relativePath));

    public static string Normalize(string relativePath) => relativePath.Replace('\\', '/');

    /// <summary>
    /// Lists the directory recursively, skipping hidden files (the manifest included) and inaccessible
    /// entries. A missing directory gives an empty snapshot. Blocking IO: call it off the UI thread.
    /// </summary>
    public static DiskSnapshot Scan(string directory)
    {
        if (!Directory.Exists(directory)) return Empty;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            MatchType = MatchType.Simple,
        };
        var files = Directory.EnumerateFiles(directory, "*", options)
            .Select(f => Path.GetRelativePath(directory, f));
        return new DiskSnapshot(files, PlatformComparer);
    }
}
