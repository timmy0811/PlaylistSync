using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PlaylistSync.App.ViewModels;

namespace PlaylistSync.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    // Scroll position of each playlist's track list, kept for this session only (every playlist opens at the
    // top after a restart). Keyed by the playlist's TrackListViewModel, which lives as long as the playlist.
    private readonly Dictionary<TrackListViewModel, double> _trackScroll = new();
    // Set while switching playlists, so the old offset (clamped to the new list) isn't saved for the new one.
    private bool _restoringScroll;
    private int _restoreGeneration;

    // Drag & drop reordering of the sidebar: the row moves live while dragging; the order is saved on release.
    private const double DragThreshold = 6;
    private PlaylistItemViewModel? _dragItem;
    private Point _dragStart;
    private bool _dragging;

    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                await vm.InitializeAsync();
        };
        Closing += OnClosing;
        TrackList.AddHandler(ScrollViewer.ScrollChangedEvent, OnTrackListScrollChanged);
        TrackList.DataContextChanged += (_, _) => RestoreTrackScroll();

        // Tunnel, so the list box's own selection handling still runs.
        PlaylistList.AddHandler(PointerPressedEvent, OnSidebarPointerPressed, RoutingStrategies.Tunnel);
        PlaylistList.AddHandler(PointerMovedEvent, OnSidebarPointerMoved, RoutingStrategies.Tunnel);
        PlaylistList.AddHandler(PointerReleasedEvent, OnSidebarPointerReleased, RoutingStrategies.Tunnel);
        PlaylistList.AddHandler(PointerCaptureLostEvent, OnSidebarCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnSidebarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragItem = null;
        if (!e.GetCurrentPoint(PlaylistList).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual source) return;
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not PlaylistItemViewModel item) return;
        _dragItem = item;
        _dragStart = e.GetPosition(PlaylistList);
    }

    private void OnSidebarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null || DataContext is not MainWindowViewModel vm) return;
        if (!e.GetCurrentPoint(PlaylistList).Properties.IsLeftButtonPressed)
        {
            _ = EndDragAsync();
            return;
        }

        var pos = e.GetPosition(PlaylistList);
        if (!_dragging)
        {
            if (Math.Abs(pos.Y - _dragStart.Y) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(PlaylistList);
            PlaylistList.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        }
        e.Handled = true;

        var from = vm.Playlists.IndexOf(_dragItem);
        if (from < 0) return;
        var to = TargetIndex(pos.Y, vm.Playlists.Count);
        if (to >= 0 && to != from) vm.PreviewMove(from, to);
    }

    /// <summary>The index of the row under <paramref name="y"/> (clamped to the first / last row); -1 if none is realized.</summary>
    private int TargetIndex(double y, int count)
    {
        int first = -1, last = -1;
        for (var i = 0; i < count; i++)
        {
            if (PlaylistList.ContainerFromIndex(i) is not Control c || c.TranslatePoint(default, PlaylistList) is not { } top) continue;
            if (first < 0) first = i;
            last = i;
            if (y < top.Y) return i == first ? i : -1;
            if (y < top.Y + c.Bounds.Height) return i;
        }
        return last;
    }

    private async void OnSidebarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging) e.Handled = true;
        e.Pointer.Capture(null);
        await EndDragAsync();
    }

    private async void OnSidebarCaptureLost(object? sender, PointerCaptureLostEventArgs e) => await EndDragAsync();

    private async Task EndDragAsync()
    {
        var dragged = _dragging;
        _dragItem = null;
        _dragging = false;
        PlaylistList.Cursor = null;
        if (dragged && DataContext is MainWindowViewModel vm) await vm.CommitOrderAsync();
    }

    private ScrollViewer? TrackScrollViewer => TrackList.FindDescendantOfType<ScrollViewer>();

    private void OnTrackListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_restoringScroll || TrackList.DataContext is not TrackListViewModel tracks) return;
        if (e.Source is not ScrollViewer sv || sv != TrackScrollViewer) return;
        _trackScroll[tracks] = sv.Offset.Y;
    }

    // Another playlist was selected: show its list where the user left it (top if not opened yet this session).
    private void RestoreTrackScroll()
    {
        var generation = ++_restoreGeneration;
        _restoringScroll = true;
        var tracks = TrackList.DataContext as TrackListViewModel;
        var target = tracks is not null && _trackScroll.TryGetValue(tracks, out var y) ? y : 0;
        // Runs at Normal priority, i.e. after the ItemsSource binding has switched to the new list but before the
        // next frame is rendered, so the list appears at its position without a visible jump. Layout is forced
        // synchronously; a few passes let the virtualized extent settle so a far-down offset isn't clamped.
        void Apply()
        {
            if (generation != _restoreGeneration) return;
            try
            {
                if (TrackScrollViewer is not { } sv) return;
                for (var pass = 0; pass < 5; pass++)
                {
                    TrackList.UpdateLayout();
                    sv.Offset = new Vector(sv.Offset.X, target);
                    TrackList.UpdateLayout();
                    if (Math.Abs(sv.Offset.Y - target) <= 1) break;
                }
            }
            finally
            {
                _restoringScroll = false;
            }
        }
        Dispatcher.UIThread.Post(Apply, DispatcherPriority.Normal);
    }

    // Double-clicking a track row plays its file (ignored on the row's buttons).
    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not TrackRowViewModel row) return;
        if (!row.PlayCommand.CanExecute(null)) return;
        e.Handled = true;
        row.PlayCommand.Execute(null);
    }

    // A running sync must be stopped first, so no yt-dlp process outlives the app.
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || DataContext is not MainWindowViewModel { IsAnythingRunning: true } vm) return;
        e.Cancel = true;
        var dialogs = App.Services.GetService(typeof(Services.IDialogService)) as Services.IDialogService;
        if (dialogs is not null && !await dialogs.ConfirmAsync("Quit Playlist Sync",
                "A download is still running. Stop it and quit?\n\nFinished tracks are kept; unfinished ones resume next time.",
                "Stop and quit"))
            return;

        IsEnabled = false;
        await vm.StopAllSyncsAsync();
        _closeConfirmed = true;
        Close();
    }
}
