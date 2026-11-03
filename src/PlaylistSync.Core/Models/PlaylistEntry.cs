namespace PlaylistSync.Core.Models;

/// <summary>One playlist ↔ local directory pair from the library.</summary>
public sealed record PlaylistEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string Url { get; init; }
    public required string Directory { get; init; }
    public DateTimeOffset? LastSync { get; init; }
    /// <summary>Full override of the global download options; null = use global.</summary>
    public DownloadOptions? Overrides { get; init; }
}

public sealed record Library
{
    public int Version { get; init; } = 1;
    public List<PlaylistEntry> Playlists { get; init; } = [];
}
