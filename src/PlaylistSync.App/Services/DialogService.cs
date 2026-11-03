using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using PlaylistSync.App.ViewModels;
using PlaylistSync.App.Views;

namespace PlaylistSync.App.Services;

/// <summary>Windows, pickers and shell actions, kept out of the view models.</summary>
public interface IDialogService
{
    /// <summary>Shows the add/edit dialog. True if the user saved.</summary>
    Task<bool> ShowPlaylistEditorAsync(PlaylistEditorViewModel editor);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);

    Task ShowErrorAsync(string title, string message);

    /// <summary>Returns a local folder path, or null if cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startDirectory);

    /// <summary>Opens the folder in the system file manager. False if that failed.</summary>
    Task<bool> OpenFolderAsync(string path);

    Task CopyTextAsync(string text);
}

public sealed class DialogService(ILogger<DialogService> log) : IDialogService
{
    private static Window Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
        ?? throw new InvalidOperationException("No main window.");

    public async Task<bool> ShowPlaylistEditorAsync(PlaylistEditorViewModel editor)
    {
        var window = new PlaylistEditorWindow { DataContext = editor };
        return await window.ShowDialog<bool?>(Owner) == true;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
    {
        var window = new MessageWindow(title, message, confirmText, "Cancel", destructive);
        return await window.ShowDialog<bool?>(Owner) == true;
    }

    public Task ShowErrorAsync(string title, string message) =>
        new MessageWindow(title, message, "OK", cancelText: null, destructive: false).ShowDialog<bool?>(Owner);

    public async Task<string?> PickFolderAsync(string title, string? startDirectory)
    {
        var storage = Owner.StorageProvider;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (startDirectory is not null && Directory.Exists(startDirectory))
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(startDirectory);

        var picked = await storage.OpenFolderPickerAsync(options);
        if (picked.Count == 0) return null;
        var path = picked[0].TryGetLocalPath();
        if (path is null) log.LogWarning("Picked folder {Uri} has no local path", picked[0].Path);
        return path;
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

    public async Task CopyTextAsync(string text)
    {
        if (Owner.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
