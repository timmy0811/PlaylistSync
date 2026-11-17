using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PlaylistSync.Core.Locations;

/// <summary>
/// A network folder such as <c>smb://user@nas/share/Music/Mix</c>. Path segments are kept decoded (as file managers
/// show them), so <see cref="ToString"/> is the human-readable form that is stored in the library.
/// </summary>
public sealed partial record RemoteAddress(string Scheme, string? User, string Host, int? Port, IReadOnlyList<string> Segments)
{
    /// <summary>Schemes that kio-fuse / gvfs can mount. Windows only supports <c>smb</c> (as a UNC path).</summary>
    public static readonly IReadOnlyList<string> SupportedSchemes = ["smb", "sftp", "ftp", "webdav", "webdavs"];

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*://")]
    private static partial Regex SchemePrefix();

    /// <summary>True if the text starts with <c>scheme://</c> (any scheme, including <c>file://</c>).</summary>
    public static bool LooksLikeUri(string? text) => text is not null && SchemePrefix().IsMatch(text.TrimStart());

    /// <summary>True for an SMB address that names only the share (<c>smb://nas/share</c>): like a drive root.</summary>
    public bool IsRoot => Segments.Count <= (Scheme == "smb" ? 1 : 0);

    /// <summary>
    /// Parses <c>scheme://[user@]host[:port]/path</c>. Percent escapes in the path are decoded. Returns null for
    /// unsupported schemes, a missing host or share, and <c>.</c>/<c>..</c> segments.
    /// </summary>
    public static RemoteAddress? Parse(string? text)
    {
        if (!LooksLikeUri(text)) return null;
        var s = text!.Trim();
        var sep = s.IndexOf("://", StringComparison.Ordinal);
        var scheme = s[..sep].ToLowerInvariant();
        if (scheme == "dav") scheme = "webdav";
        else if (scheme == "davs") scheme = "webdavs";
        if (!SupportedSchemes.Contains(scheme)) return null;

        // No query/fragment handling: '?' and '#' are taken literally, as folder name characters.
        var rest = s[(sep + 3)..];
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "" : rest[(slash + 1)..];

        string? user = null;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            user = Unescape(authority[..at]);
            authority = authority[(at + 1)..];
            if (user.Length == 0) user = null;
        }

        int? port = null;
        var colon = authority.LastIndexOf(':');
        if (colon >= 0 && !authority.EndsWith(']'))
        {
            if (!int.TryParse(authority[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
                return null;
            port = p;
            authority = authority[..colon];
        }
        var host = authority.ToLowerInvariant();
        if (host.Length == 0 || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@')) return null;

        var segments = new List<string>();
        foreach (var raw in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = Unescape(raw);
            if (seg is "." or ".." || seg.Contains('/') || seg.Contains('\0')) return null;
            segments.Add(seg);
        }
        if (scheme == "smb" && segments.Count == 0) return null; // smb needs at least the share

        return new RemoteAddress(scheme, user, host, port, segments);
    }

    /// <summary>The part that gets mounted: the share for SMB, the server otherwise.</summary>
    public RemoteAddress MountRoot => this with { Segments = Segments.Take(Scheme == "smb" ? 1 : 0).ToList() };

    /// <summary>The segments below <see cref="MountRoot"/>.</summary>
    public IReadOnlyList<string> SubPath => Segments.Skip(Scheme == "smb" ? 1 : 0).ToList();

    /// <summary>Human-readable form (decoded path), used for storage and display.</summary>
    public override string ToString() => Build(escape: false);

    /// <summary>RFC 3986 form with escaped segments, for handing to kio-fuse / gio / a file manager.</summary>
    public string ToUriString() => Build(escape: true);

    /// <summary><c>\\host\share\path</c> for SMB (the user name is dropped, Windows asks for credentials itself).</summary>
    public string? ToUnc() =>
        Scheme == "smb" && Port is null ? @"\\" + Host + @"\" + string.Join('\\', Segments) : null;

    private string Build(bool escape)
    {
        var sb = new StringBuilder(Scheme).Append("://");
        if (User is not null) sb.Append(escape ? Uri.EscapeDataString(User) : User).Append('@');
        sb.Append(Host);
        if (Port is { } p) sb.Append(':').Append(p.ToString(CultureInfo.InvariantCulture));
        foreach (var seg in Segments) sb.Append('/').Append(escape ? Uri.EscapeDataString(seg) : seg);
        return sb.ToString();
    }

    private static string Unescape(string s) => s.Contains('%') ? Uri.UnescapeDataString(s) : s;

    // ───────────── Mount paths (a folder picked in a file dialog may come back as one) ─────────────

    [GeneratedRegex(@"^/run/user/\d+/kio-fuse-[^/]+/(?<scheme>[a-z0-9+.\-]+)/(?<authority>[^/]+)(?<rest>/.*)?$")]
    private static partial Regex KioFusePath();

    [GeneratedRegex(@"^/run/user/\d+/gvfs/(?<mount>[^/]+)(?<rest>/.*)?$")]
    private static partial Regex GvfsPath();

    /// <summary>
    /// Turns a kio-fuse (<c>/run/user/1000/kio-fuse-AbCdEf/smb/user@nas/share/…</c>) or gvfs
    /// (<c>/run/user/1000/gvfs/smb-share:server=nas,share=share,user=user/…</c>) mount path back into its network
    /// address. The mount path itself is not stable across sessions, the address is. Null for other paths.
    /// </summary>
    public static RemoteAddress? FromMountPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || OperatingSystem.IsWindows() && !path.StartsWith('/')) return null;

        if (KioFusePath().Match(path) is { Success: true } k)
            return Parse($"{k.Groups["scheme"].Value}://{k.Groups["authority"].Value}{k.Groups["rest"].Value}");

        if (GvfsPath().Match(path) is not { Success: true } g) return null;
        var mount = g.Groups["mount"].Value;
        var colon = mount.IndexOf(':');
        if (colon < 0) return null;
        var type = mount[..colon];
        var keys = mount[(colon + 1)..].Split(',')
            .Select(kv => kv.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0], StringComparer.Ordinal)
            .ToDictionary(grp => grp.Key, grp => Unescape(grp.First()[1]), StringComparer.Ordinal);
        keys.TryGetValue("user", out var user);
        var userPart = string.IsNullOrEmpty(user) ? "" : user + "@";
        var portPart = keys.TryGetValue("port", out var port) ? ":" + port : "";
        var restPart = g.Groups["rest"].Value;

        var uri = type switch
        {
            "smb-share" when keys.TryGetValue("server", out var server) && keys.TryGetValue("share", out var share)
                => $"smb://{userPart}{server}{portPart}/{share}{restPart}",
            "sftp" or "ftp" when keys.TryGetValue("host", out var host)
                => $"{type}://{userPart}{host}{portPart}{restPart}",
            _ => null,
        };
        return Parse(uri);
    }
}
