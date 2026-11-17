using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PlaylistSync.Core.Download;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Sync;

namespace PlaylistSync.App.ViewModels;

/// <summary>A filter chip above the track list.</summary>
public sealed partial class FilterChipViewModel(TrackFilter filter, string label) : ViewModelBase
{
    public TrackFilter Filter { get; } = filter;
    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private int _count;

    public string Text => $"{Label}  {Count}";
}

/// <summary>
/// The tracks of one playlist: all rows, the visible (filtered + searched) rows and the chip counts.
/// Lives as long as the playlist item, so a running sync keeps updating it while another playlist is shown.
/// UI thread only.
/// </summary>
public sealed partial class TrackListViewModel : ViewModelBase
{
    private readonly ITrackActions _actions;
    private readonly List<TrackRowViewModel> _rows = [];
    private readonly Dictionary<string, TrackRowViewModel> _byId = new(StringComparer.Ordinal);

    public TrackListViewModel(ITrackActions actions)
    {
        _actions = actions;
        Chips =
        [
            new(TrackFilter.All, "All"),
            new(TrackFilter.Removed, "Removed"),
            new(TrackFilter.Problems, "Failed"),
            new(TrackFilter.Unavailable, "Unavailable"),
            new(TrackFilter.Untracked, "Untracked"),
            new(TrackFilter.Duplicates, "Duplicates"),
        ];
        _selectedChip = Chips[0];
    }

    public IReadOnlyList<FilterChipViewModel> Chips { get; }

    private FilterChipViewModel _selectedChip;

    /// <summary>
    /// The selected filter. Never null: a list box clears its selection while its items are swapped (when another
    /// playlist is shown), and that must not lose the filter.
    /// </summary>
    public FilterChipViewModel? SelectedChip
    {
        get => _selectedChip;
        set
        {
            if (value is null)
            {
                // Push the real value back into the view once the swap is done.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SelectedChip)));
                return;
            }
            if (!SetProperty(ref _selectedChip, value)) return;
            OnPropertyChanged(nameof(IsRemovedFilter));
            OnPropertyChanged(nameof(EmptyText));
            ApplyFilter();
        }
    }

    private TrackFilter Filter => _selectedChip.Filter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText), nameof(IsSearching))]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(EmptyText))]
    private ObservableCollection<TrackRowViewModel> _visible = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(EmptyText))]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeletableRemoved), nameof(DeleteRemovedText))]
    private int _deletableRemovedCount;

    public bool IsEmpty => IsLoaded && Visible.Count == 0;
    public bool IsRemovedFilter => Filter == TrackFilter.Removed;
    public bool HasDeletableRemoved => DeletableRemovedCount > 0;
    public string DeleteRemovedText => DeletableRemovedCount == 1 ? "Delete 1 file…" : $"Delete {DeletableRemovedCount} files…";

    public string EmptyText =>
        !string.IsNullOrWhiteSpace(SearchText) ? "No tracks match the search."
        : _rows.Count == 0 ? "No tracks yet. Press Sync to download the playlist."
        : Filter switch
        {
            TrackFilter.Removed => "No tracks were removed from the remote playlist.",
            TrackFilter.Problems => "No failed downloads or missing files.",
            TrackFilter.Unavailable => "No unavailable tracks.",
            TrackFilter.Untracked => "No other audio files in the folder.",
            TrackFilter.Duplicates => "No song is listed twice.",
            _ => "No tracks.",
        };

    /// <summary>Ids of removed-in-remote tracks whose file can be deleted.</summary>
    public IReadOnlyList<TrackRowViewModel> DeletableRemoved =>
        _rows.Where(r => r.IsRemoved && r.CanDelete).ToList();

    public bool Contains(TrackRowViewModel row) => _rows.Contains(row);

    /// <summary>
    /// Tracks of the remote playlist that have a local file, in playlist order: what Play / Shuffle queue up.
    /// Removed-in-remote tracks and untracked files are left out. The files may still be missing on disk.
    /// </summary>
    public IReadOnlyList<TrackRowViewModel> Playable =>
        _rows.Where(r => r is { HasId: true, IsRemoved: false, File: not null }).ToList();

    /// <summary>True while the search box holds text, so the list shows only matching tracks.</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    /// What Play / Shuffle queue up: <see cref="Playable"/>, or while searching, the rows currently shown (current
    /// filter and search, in display order) that have a file, like <see cref="QueueFrom"/>.
    /// </summary>
    public IReadOnlyList<TrackRowViewModel> PlaybackQueue => IsSearching
        ? Visible.Where(r => r is { File: not null, Status: not (TrackStatus.MissingLocal or TrackStatus.Downloading) }).ToList()
        : Playable;

    /// <summary>
    /// The play queue for "Play" on <paramref name="start"/>: that row and the rows shown after it (current filter and
    /// search, in display order) that have a file. Uses only what the rows already know, with no disk access, so it is
    /// instant even for large playlists or network folders; players skip a file that has gone missing since.
    /// </summary>
    public IReadOnlyList<TrackRowViewModel> QueueFrom(TrackRowViewModel start)
    {
        var index = Visible.IndexOf(start);
        if (index < 0) return [start];
        var queue = new List<TrackRowViewModel>(Visible.Count - index) { start };
        for (var i = index + 1; i < Visible.Count; i++)
        {
            var row = Visible[i];
            if (row is { File: not null, Status: not (TrackStatus.MissingLocal or TrackStatus.Downloading) }) queue.Add(row);
        }
        return queue;
    }

    /// <summary>Replaces all rows (after loading or when a sync was planned / finished).</summary>
    public void Reset(IReadOnlyList<TrackListItem> items)
    {
        _rows.Clear();
        _byId.Clear();
        foreach (var item in items)
        {
            var row = new TrackRowViewModel(item, _actions);
            _rows.Add(row);
            if (row.Id is not null) _byId[row.Id] = row;
        }
        foreach (var row in _rows) ResolveDuplicateTitle(row);
        IsLoaded = true;
        Refresh();
    }

    /// <summary>A track record changed during a sync.</summary>
    public void Update(string id, TrackRecord record)
    {
        if (!_byId.TryGetValue(id, out var row)) return;
        var filter = Filter;
        var wasVisible = TrackList.Matches(filter, row.Status);
        row.Update(record);
        ResolveDuplicateTitle(row);
        UpdateCounts();
        // Only rebuild the visible list if the row moved in or out of the current filter.
        if (wasVisible != TrackList.Matches(filter, row.Status)) ApplyFilter();
    }

    private void ResolveDuplicateTitle(TrackRowViewModel row) =>
        row.DuplicateOfTitle = row.DuplicateOf is { } id && _byId.TryGetValue(id, out var original) ? original.DisplayTitle : null;

    public void SetProgress(string id, DownloadProgress progress)
    {
        if (_byId.TryGetValue(id, out var row) && row.IsDownloading) row.SetProgress(progress);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void Refresh()
    {
        UpdateCounts();
        ApplyFilter();
        OnPropertyChanged(nameof(EmptyText));
    }

    private void UpdateCounts()
    {
        foreach (var chip in Chips)
            chip.Count = chip.Filter == TrackFilter.All ? _rows.Count : _rows.Count(r => TrackList.Matches(chip.Filter, r.Status));
        DeletableRemovedCount = _rows.Count(r => r.IsRemoved && r.CanDelete);
    }

    private void ApplyFilter()
    {
        var filter = Filter;
        var query = SearchText;
        Visible = new ObservableCollection<TrackRowViewModel>(_rows.Where(r =>
            TrackList.Matches(filter, r.Status) &&
            TrackList.MatchesSearch(query, Item(r))));
    }

    // MatchesSearch needs a TrackListItem; build a light one from the row's current values.
    private static TrackListItem Item(TrackRowViewModel r) => r.IsUntracked
        ? TrackListItem.Untracked(r.UntrackedFile!)
        : TrackListItem.Track(r.Id!, new TrackRecord { Title = r.Title, Uploader = r.Uploader, File = r.File });
}
