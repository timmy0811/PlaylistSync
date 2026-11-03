namespace PlaylistSync.Core;

/// <summary>
/// Platform-specific locations for config, data (managed binaries) and logs.
/// Linux follows the XDG base directory spec; Windows uses %APPDATA% / %LOCALAPPDATA%.
/// </summary>
public sealed class AppPaths
{
    public const string AppDirName = "playlist-sync";
    public const string WindowsAppDirName = "PlaylistSync";

    public string ConfigDir { get; }
    public string DataDir { get; }
    public string LogDir => Path.Combine(DataDir, "logs");
    public string BinDir => Path.Combine(DataDir, "bin");
    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public string LibraryFile => Path.Combine(ConfigDir, "library.json");

    public AppPaths(string configDir, string dataDir)
    {
        ConfigDir = configDir;
        DataDir = dataDir;
    }

    /// <summary>Resolves the default paths for the current OS.</summary>
    public static AppPaths ForCurrentPlatform() =>
        Resolve(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable, GetFolder);

    /// <summary>Testable resolution logic.</summary>
    internal static AppPaths Resolve(
        bool isWindows,
        Func<string, string?> getEnv,
        Func<Environment.SpecialFolder, string> getFolder)
    {
        if (isWindows)
        {
            return new AppPaths(
                Path.Combine(getFolder(Environment.SpecialFolder.ApplicationData), WindowsAppDirName),
                Path.Combine(getFolder(Environment.SpecialFolder.LocalApplicationData), WindowsAppDirName));
        }

        var home = getFolder(Environment.SpecialFolder.UserProfile);
        var config = NonEmptyAbsolute(getEnv("XDG_CONFIG_HOME")) ?? Path.Combine(home, ".config");
        var data = NonEmptyAbsolute(getEnv("XDG_DATA_HOME")) ?? Path.Combine(home, ".local", "share");
        return new AppPaths(Path.Combine(config, AppDirName), Path.Combine(data, AppDirName));
    }

    /// <summary>Creates all directories if missing.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(BinDir);
    }

    // The XDG spec says relative paths must be ignored.
    private static string? NonEmptyAbsolute(string? value) =>
        string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) ? null : value;

    private static string GetFolder(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
}
