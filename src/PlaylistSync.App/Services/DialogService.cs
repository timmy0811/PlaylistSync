using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using PlaylistSync.App.ViewModels;
using PlaylistSync.App.Views;
using PlaylistSync.Core.Locations;

namespace PlaylistSync.App.Services;

/// <summary>Windows, pickers and shell actions, kept out of the view models.</summary>
public interface IDialogService
{
    /// <summary>Shows the add/edit dialog. True if the user saved.</summary>
    Task<bool> ShowPlaylistEditorAsync(PlaylistEditorViewModel editor);

    /// <summary>Shows the settings dialog (global or one playlist's). True if the user saved.</summary>
    Task<bool> ShowSettingsAsync(SettingsViewModel settings);

    /// <summary>Shows the toolbox window (non-modal, one at a time; brings it to the front if already open).</summary>
    void ShowToolbox(ToolboxViewModel toolbox);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);

    Task ShowErrorAsync(string title, string message);

    /// <summary>Returns a local folder path, or null if cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startDirectory);

    /// <summary>Returns a local file path, or null if cancelled.</summary>
    Task<string?> PickFileAsync(string title, string? startDirectory);

    /// <summary>Opens the folder in the system file manager. False if that failed.</summary>
    Task<bool> OpenFolderAsync(string path);

    /// <summary>Opens a file with its default application (e.g. a music player). False if that failed.</summary>
    Task<bool> OpenFileAsync(string path);

    /// <summary>Opens a URI (e.g. an smb:// folder) with its default handler. False if that failed.</summary>
    Task<bool> OpenUriAsync(string uri);

    Task CopyTextAsync(string text);
}

public sealed class DialogService(ILogger<DialogService> log) : IDialogService
{
    private static Window Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
        ?? throw new InvalidOperationException("No main window.");

    /// <summary>
    /// The active dialog if one is open (e.g. the playlist editor opening its download settings, or a picker from the
    /// settings dialog), so nested dialogs stack on top of it instead of the main window.
    /// </summary>
    private static Window TopWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .LastOrDefault(w => w.IsVisible) ?? Owner;

    public async Task<bool> ShowPlaylistEditorAsync(PlaylistEditorViewModel editor)
    {
        var window = new PlaylistEditorWindow { DataContext = editor };
        return await window.ShowDialog<bool?>(TopWindow) == true;
    }

    public async Task<bool> ShowSettingsAsync(SettingsViewModel settings)
    {
        var window = new SettingsWindow { DataContext = settings };
        return await window.ShowDialog<bool?>(TopWindow) == true;
    }

    private ToolboxWindow? _toolbox;

    public void ShowToolbox(ToolboxViewModel toolbox)
    {
        if (_toolbox is not null)
        {
            _toolbox.Activate();
            return;
        }
        toolbox.Load();
        _toolbox = new ToolboxWindow { DataContext = toolbox };
        _toolbox.Closed += (_, _) => _toolbox = null;
        _toolbox.Show(Owner);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
    {
        var window = new MessageWindow(title, message, confirmText, "Cancel", destructive);
        return await window.ShowDialog<bool?>(TopWindow) == true;
    }

    public Task ShowErrorAsync(string title, string message) =>
        new MessageWindow(title, message, "OK", cancelText: null, destructive: false).ShowDialog<bool?>(TopWindow);

    public async Task<string?> PickFileAsync(string title, string? startDirectory)
    {
        var storage = TopWindow.StorageProvider;
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
        if (startDirectory is not null && Directory.Exists(startDirectory))
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(startDirectory);

        var picked = await storage.OpenFilePickerAsync(options);
        if (picked.Count == 0) return null;
        var path = picked[0].TryGetLocalPath();
        if (path is null) log.LogWarning("Picked file {Uri} has no local path", picked[0].Path);
        return path;
    }

    public async Task<string?> PickFolderAsync(string title, string? startDirectory)
    {
        var storage = TopWindow.StorageProvider;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (startDirectory is not null && Directory.Exists(startDirectory))
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(startDirectory);

        var picked = await storage.OpenFolderPickerAsync(options);
        if (picked.Count == 0) return null;
        var uri = picked[0].Path;
        // A network folder picked in the dialog: keep its address (a kio-fuse / gvfs mount path is turned back into
        // one too), since mount paths change between sessions.
        if (!uri.IsFile && RemoteAddress.Parse(uri.OriginalString) is { } remote) return remote.ToString();
        var path = picked[0].TryGetLocalPath();
        if (path is null) log.LogWarning("Picked folder {Uri} has no local path", uri);
        return path is not null && RemoteAddress.FromMountPath(path) is { } mounted ? mounted.ToString() : path;
    }

    public async Task<bool> OpenFolderAsync(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            return await Owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not open folder {Path}", path);
            return false;
        }
    }

    public async Task<bool> OpenFileAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            return await Owner.Launcher.LaunchFileInfoAsync(new FileInfo(path));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not open file {Path}", path);
            return false;
        }
    }

    public async Task<bool> OpenUriAsync(string uri)
    {
        try
        {
            return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && await Owner.Launcher.LaunchUriAsync(parsed);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not open {Uri}", uri);
            return false;
        }
    }

    public async Task CopyTextAsync(string text)
    {
        if (Owner.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
