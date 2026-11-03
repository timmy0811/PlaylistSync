using System.Globalization;

namespace PlaylistSync.Core.Playlists;

/// <summary>Short "2 h ago" style texts for the UI.</summary>
public static class RelativeTime
{
    public static string Format(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is null) return "never";
        var ago = now - when.Value;
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromDays(1)) return $"{(int)ago.TotalHours} h ago";
        if (ago < TimeSpan.FromDays(2)) return "yesterday";
        if (ago < TimeSpan.FromDays(30)) return $"{(int)ago.TotalDays} days ago";
        return when.Value.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }
}
