using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlaylistSync.App.Services;
using PlaylistSync.Core.Models;
using PlaylistSync.Core.Playlists;
using PlaylistSync.Core.Sources;
using PlaylistSync.Core.Storage;

namespace PlaylistSync.App.ViewModels;

public partial class MainWindowViewModel(
    PlaylistLibrary library,
    SettingsService settings,
    ManifestStore manifests,
    IEnumerable<IPlaylistSource> sources,
    IDialogService dialogs,
    ILogger<MainWindowViewModel> log) : ViewModelBase
{
    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(EditPlaylistCommand), nameof(RemovePlaylistCommand), nameof(OpenFolderCommand),
        nameof(CopyUrlCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private PlaylistItemViewModel? _selectedPlaylist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    [ObservableProperty]
    private string _statusText = "Loading…";

    public bool HasSelection => SelectedPlaylist is not null;
    public bool IsEmpty => IsLoaded && Playlists.Count == 0;

    /// <summary>Loads settings and the library. Called once when the main window opens.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await settings.LoadAsync();
            await library.LoadAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Loading the library failed");
            StatusText = "Could not load the library";
            await dialogs.ShowErrorAsync("Could not load the library", ex.Message);
            return;
        }

        foreach (var entry in library.Entries)
            Playlists.Add(new PlaylistItemViewModel(entry));
        IsLoaded = true;
        OnPropertyChanged(nameof(IsEmpty));
        SelectedPlaylist = Playlists.FirstOrDefault();
        StatusText = "Ready";

        await Task.WhenAll(Playlists.Select(p => p.RefreshSummaryAsync(manifests)));
    }

    [RelayCommand]
    private async Task AddPlaylistAsync()
    {
        var editor = CreateEditor(original: null, async entry =>
        {
            await library.AddAsync(entry);
            var item = new PlaylistItemViewModel(entry);
            Playlists.Add(item);
            OnPropertyChanged(nameof(IsEmpty));
            SelectedPlaylist = item;
            log.LogInformation("Added playlist '{Name}' ({Url} → {Dir})", entry.Name, entry.Url, entry.Directory);
            _ = item.RefreshSummaryAsync(manifests);
        });
        await dialogs.ShowPlaylistEditorAsync(editor);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditPlaylistAsync()
    {
        if (SelectedPlaylist is not { } item) return;
        var editor = CreateEditor(item.Entry, async entry =>
        {
            await library.UpdateAsync(entry);
            item.Entry = entry;
            log.LogInformation("Updated playlist '{Name}' ({Url} → {Dir})", entry.Name, entry.Url, entry.Directory);
            _ = item.RefreshSummaryAsync(manifests);
        });
        await dialogs.ShowPlaylistEditorAsync(editor);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemovePlaylistAsync()
    {
        if (SelectedPlaylist is not { } item) return;
        var ok = await dialogs.ConfirmAsync(
            "Remove playlist",
            $"Remove '{item.Name}' from Playlist Sync?\n\nThe folder {item.DisplayDirectory} and all files in it are kept. " +
            "If you add the folder again later, its sync history is picked up again.",
            "Remove", destructive: true);
        if (!ok) return;

        try
        {
            await library.RemoveAsync(item.Id);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Removing playlist '{Name}' failed", item.Name);
            await dialogs.ShowErrorAsync("Could not remove the playlist", ex.Message);
            return;
        }

        var index = Playlists.IndexOf(item);
        Playlists.Remove(item);
        OnPropertyChanged(nameof(IsEmpty));
        SelectedPlaylist = Playlists.Count == 0 ? null : Playlists[Math.Min(index, Playlists.Count - 1)];
        log.LogInformation("Removed playlist '{Name}' from the library (folder kept)", item.Name);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task MoveUpAsync() => MoveAsync(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task MoveDownAsync() => MoveAsync(+1);

    private async Task MoveAsync(int delta)
    {
        if (SelectedPlaylist is not { } item) return;
        var from = Playlists.IndexOf(item);
        var to = from + delta;
        if (to < 0 || to >= Playlists.Count) return;
        try
        {
            await library.MoveAsync(item.Id, to);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Reordering failed");
            await dialogs.ShowErrorAsync("Could not save the new order", ex.Message);
            return;
        }
        Playlists.Move(from, to);
        SelectedPlaylist = item;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpenFolderAsync()
    {
        if (SelectedPlaylist is not { } item) return;
        if (!Directory.Exists(item.Directory))
        {
            StatusText = $"The folder {item.DisplayDirectory} doesn't exist yet. It is created on the first sync.";
            return;
        }
        if (!await dialogs.OpenFolderAsync(item.Directory))
            StatusText = $"Could not open {item.DisplayDirectory}";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyUrlAsync()
    {
        if (SelectedPlaylist is not { } item) return;
        await dialogs.CopyTextAsync(item.Url);
        StatusText = "URL copied";
    }

    private PlaylistEditorViewModel CreateEditor(PlaylistEntry? original, Func<PlaylistEntry, Task> save) =>
        new(sources, manifests, dialogs, log, settings.Current, library.Entries, original, save);
}
