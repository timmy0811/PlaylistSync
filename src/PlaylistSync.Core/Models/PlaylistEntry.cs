namespace PlaylistSync.Core.Models;

/// <summary>One playlist ↔ local directory pair from the library.</summary>
public sealed record PlaylistEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string Url { get; init; }
    public required string Directory { get; init; }
    public DateTimeOffset? LastSync { get; init; }
    /// <summary>
    /// The playlist's own download options; null = use the global ones. Network settings are always global,
    /// see <see cref="DownloadOptions.Effective"/>.
    /// </summary>
    public DownloadOptions? Overrides { get; init; }

    public DownloadOptions EffectiveOptions(AppSettings settings) => DownloadOptions.Effective(settings.Download, Overrides);
}

public sealed record Library
{
    public int Version { get; init; } = 1;
    public List<PlaylistEntry> Playlists { get; init; } = [];
}
