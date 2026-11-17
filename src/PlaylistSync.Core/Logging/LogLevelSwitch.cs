using Microsoft.Extensions.Logging;

namespace PlaylistSync.Core.Logging;

/// <summary>The minimum log level, changeable at runtime from the settings (used as a logging filter).</summary>
public sealed class LogLevelSwitch(LogLevel initial = LogLevel.Information)
{
    private volatile int _level = (int)initial;

    public LogLevel MinimumLevel
    {
        get => (LogLevel)_level;
        set => _level = (int)value;
    }

    public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= MinimumLevel;
}
