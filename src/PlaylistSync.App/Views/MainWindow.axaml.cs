using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
