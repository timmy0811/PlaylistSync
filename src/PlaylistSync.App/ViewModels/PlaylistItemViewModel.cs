using CommunityToolkit.Mvvm.ComponentModel;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playlists;
using PlaylistSync.Core.Storage;

namespace PlaylistSync.App.ViewModels;

public enum PlaylistHealth { NeverSynced, Ok, Attention, Error }

/// <summary>One library entry in the sidebar and the detail header.</summary>
public sealed partial class PlaylistItemViewModel(PlaylistEntry entry) : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Url), nameof(Directory), nameof(DisplayDirectory), nameof(LastSyncText))]
    private PlaylistEntry _entry = entry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(SummaryText), nameof(Health), nameof(IsOk), nameof(NeedsAttention),
        nameof(HasError), nameof(NeverSynced), nameof(HealthTip))]
    private ManifestSummary? _summary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText), nameof(Health), nameof(IsOk), nameof(NeedsAttention), nameof(HasError),
        nameof(NeverSynced), nameof(HealthTip))]
    private string? _manifestError;

    public Guid Id => Entry.Id;
    public string Name => Entry.Name;
    public string Url => Entry.Url;
    public string Directory => Entry.Directory;
    public string DisplayDirectory => ShortenHome(Entry.Directory);
    public string LastSyncText => "Last sync " + RelativeTime.Format(Entry.LastSync, DateTimeOffset.Now);

    public string CountText => Summary is { InPlaylist: > 0 } s ? s.InPlaylist.ToString(System.Globalization.CultureInfo.CurrentCulture) : "—";

    public string SummaryText => ManifestError ?? (Summary is { } s && (s.InPlaylist > 0 || s.RemovedRemote > 0)
        ? s.Describe()
        : "Not synced yet");

    public PlaylistHealth Health => ManifestError is not null ? PlaylistHealth.Error
        : Summary is null || Summary.InPlaylist + Summary.RemovedRemote == 0 ? PlaylistHealth.NeverSynced
        : Summary.NeedsAttention ? PlaylistHealth.Attention
        : PlaylistHealth.Ok;

    // Separate flags so the view can bind style classes.
    public bool IsOk => Health == PlaylistHealth.Ok;
    public bool NeedsAttention => Health == PlaylistHealth.Attention;
    public bool HasError => Health == PlaylistHealth.Error;
    public bool NeverSynced => Health == PlaylistHealth.NeverSynced;

    public string HealthTip => Health switch
    {
        PlaylistHealth.Ok => "Up to date",
        PlaylistHealth.Attention => "Has removed, failed or unavailable tracks",
        PlaylistHealth.Error => "The sync state can't be read",
        _ => "Not synced yet",
    };

    /// <summary>Re-reads the folder's manifest for the counts. Never throws.</summary>
    public async Task RefreshSummaryAsync(ManifestStore manifests, CancellationToken ct = default)
    {
        try
        {
            var manifest = await Task.Run(() => manifests.LoadAsync(Entry.Directory, ct), ct);
            Summary = ManifestSummary.From(manifest);
            ManifestError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
        {
            Summary = null;
            ManifestError = ex.Message;
        }
    }

    private static string ShortenHome(string path)
    {
        if (OperatingSystem.IsWindows()) return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) return path;
        if (path == home) return "~";
        var prefix = home.EndsWith(Path.DirectorySeparatorChar) ? home : home + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal) ? "~" + Path.DirectorySeparatorChar + path[prefix.Length..] : path;
    }
}
