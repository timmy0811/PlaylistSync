using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PlaylistSync.Core.Models;

namespace PlaylistSync.Core.Playback;

/// <summary>Thrown when the configured player can't be started; the message is shown to the user.</summary>
public sealed class PlayerLaunchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Starts the player chosen in the settings with one file (a track or an .m3u queue). The player runs on its own: the
/// app neither waits for it nor stops it. The system-default case is handled by the UI (the platform launcher).
/// </summary>
public sealed class PlayerLauncher(ILogger<PlayerLauncher> log)
{
    /// <exception cref="PlayerLaunchException">The player is not installed any more or failed to start.</exception>
    public void Launch(PlaybackSettings settings, string file)
    {
        string program;
        IReadOnlyList<string> args;
        if (!string.IsNullOrWhiteSpace(settings.DesktopId))
        {
            var entry = PlayerCatalog.Find(PlayerCatalog.ApplicationDirs(), settings.DesktopId)
                        ?? throw new PlayerLaunchException(
                            $"The player '{settings.DesktopId}' is not installed any more. Choose another one in Settings → App.");
            (program, args) = entry.BuildCommand(file)
                              ?? throw new PlayerLaunchException($"Can't read how to start {entry.Name} (Exec line of {entry.FilePath}).");
        }
        else if (!string.IsNullOrWhiteSpace(settings.ExecutablePath))
        {
            program = settings.ExecutablePath.Trim();
            args = [file];
        }
        else
        {
            throw new InvalidOperationException("No player configured; use the system default launcher.");
        }

        var psi = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        try
        {
            log.LogInformation("Starting player {Program} {Args}", program, args);
            using var process = Process.Start(psi)
                                ?? throw new PlayerLaunchException($"Could not start {program}.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            log.LogWarning(ex, "Starting the player {Program} failed", program);
            throw new PlayerLaunchException($"Could not start {program}: {ex.Message}", ex);
        }
    }
}
