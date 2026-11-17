using PlaylistSync.Core.Models;

namespace PlaylistSync.Core.Playlists;

/// <summary>Track counts of a manifest, for the sidebar and the playlist header.</summary>
public sealed record ManifestSummary(
    int Synced,
    int Pending,
    int Failed,
    int RemovedRemote,
    int Unavailable,
    int MissingLocal,
    int Duplicate = 0)
{
    public static ManifestSummary Empty { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>Tracks currently in the remote playlist (everything except removed ones).</summary>
    public int InPlaylist => Synced + Pending + Failed + Unavailable + MissingLocal + Duplicate;

    /// <summary>Something the user may want to look at.</summary>
    public bool NeedsAttention => Failed + RemovedRemote + Unavailable + MissingLocal > 0;

    public static ManifestSummary From(Manifest? manifest)
    {
        if (manifest is null) return Empty;
        int synced = 0, pending = 0, failed = 0, removed = 0, unavailable = 0, missing = 0, duplicate = 0;
        foreach (var track in manifest.Tracks.Values)
        {
            switch (track.Status)
            {
                case TrackStatus.Synced: synced++; break;
                // An interrupted download is still waiting to be downloaded.
                case TrackStatus.Pending or TrackStatus.Downloading: pending++; break;
                case TrackStatus.Failed: failed++; break;
                case TrackStatus.RemovedRemote: removed++; break;
                case TrackStatus.Unavailable: unavailable++; break;
                case TrackStatus.MissingLocal: missing++; break;
                case TrackStatus.Duplicate: duplicate++; break;
            }
        }
        return new ManifestSummary(synced, pending, failed, removed, unavailable, missing, duplicate);
    }

    /// <summary>"40 synced · 1 removed · 1 failed"; zero counts are left out.</summary>
    public string Describe()
    {
        var parts = new List<string> { $"{Synced} synced" };
        if (Pending > 0) parts.Add($"{Pending} pending");
        if (Failed > 0) parts.Add($"{Failed} failed");
        if (MissingLocal > 0) parts.Add($"{MissingLocal} missing locally");
        if (RemovedRemote > 0) parts.Add($"{RemovedRemote} removed in remote");
        if (Unavailable > 0) parts.Add($"{Unavailable} unavailable");
        if (Duplicate > 0) parts.Add(Duplicate == 1 ? "1 duplicate" : $"{Duplicate} duplicates");
        return string.Join(" · ", parts);
    }
}
