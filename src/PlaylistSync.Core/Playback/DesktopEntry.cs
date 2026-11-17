using System.Text;

namespace PlaylistSync.Core.Playback;

/// <summary>
/// The parts of a freedesktop <c>.desktop</c> file the app needs to launch a player
/// (https://specifications.freedesktop.org/desktop-entry-spec/latest/).
/// </summary>
public sealed record DesktopEntry(string Id, string FilePath, string Name, string Exec, string? Icon, IReadOnlyList<string> MimeTypes)
{
    /// <summary>
    /// Parses the <c>[Desktop Entry]</c> group. Null unless it is a visible application with an Exec line
    /// (Type=Application, not Hidden / NoDisplay).
    /// </summary>
    public static DesktopEntry? Parse(string id, string filePath, string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var inMain = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                // Later groups (actions) must not override the main group.
                if (inMain) break;
                inMain = line == "[Desktop Entry]";
                continue;
            }
            if (!inMain) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            // Localised keys (Name[de]) are skipped; the plain value is always present.
            if (key.Contains('[')) continue;
            values.TryAdd(key, Unescape(line[(eq + 1)..].Trim()));
        }

        if (values.GetValueOrDefault("Type") != "Application") return null;
        if (IsTrue(values, "Hidden") || IsTrue(values, "NoDisplay")) return null;
        if (!values.TryGetValue("Exec", out var exec) || exec.Length == 0) return null;
        if (!values.TryGetValue("Name", out var name) || name.Length == 0) return null;
        var mimes = values.TryGetValue("MimeType", out var m)
            ? m.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return new DesktopEntry(id, filePath, name, exec, values.GetValueOrDefault("Icon"), mimes);
    }

    /// <summary>Declares that it opens audio files or playlists.</summary>
    public bool HandlesAudio => MimeTypes.Any(t =>
        t.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The program and arguments to open <paramref name="file"/>: the Exec line split into arguments, with field codes
    /// expanded (%f/%F → the path, %u/%U → a file:// URL, %c → name, %i → icon, %k → desktop file, %% → %).
    /// If the Exec line has no file field code, the path is appended.
    /// </summary>
    public (string Program, IReadOnlyList<string> Args)? BuildCommand(string file)
    {
        var tokens = SplitExec(Exec);
        if (tokens is null || tokens.Count == 0) return null;

        var result = new List<string>(tokens.Count + 1);
        var hadFile = false;
        foreach (var token in tokens)
        {
            switch (token)
            {
                // A field code that stands alone expands to whole arguments (or none).
                case "%f" or "%F":
                    result.Add(file);
                    hadFile = true;
                    continue;
                case "%u" or "%U":
                    result.Add(new Uri(file).AbsoluteUri);
                    hadFile = true;
                    continue;
                case "%i":
                    if (!string.IsNullOrEmpty(Icon)) result.AddRange(["--icon", Icon]);
                    continue;
            }

            var sb = new StringBuilder();
            for (var i = 0; i < token.Length; i++)
            {
                if (token[i] != '%' || i + 1 >= token.Length)
                {
                    sb.Append(token[i]);
                    continue;
                }
                var code = token[++i];
                switch (code)
                {
                    case '%': sb.Append('%'); break;
                    case 'c': sb.Append(Name); break;
                    case 'k': sb.Append(FilePath); break;
                    case 'f' or 'F': sb.Append(file); hadFile = true; break;
                    case 'u' or 'U': sb.Append(new Uri(file).AbsoluteUri); hadFile = true; break;
                    // Deprecated / unknown codes are dropped.
                }
            }
            result.Add(sb.ToString());
        }

        if (!hadFile) result.Add(file);
        return (result[0], result.Skip(1).ToList());
    }

    /// <summary>
    /// Splits an Exec value into arguments: whitespace separates, "double quotes" group, and inside quotes
    /// <c>\" \` \$ \\</c> are escapes. Null for an unclosed quote.
    /// </summary>
    internal static List<string>? SplitExec(string exec)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var quoted = false;
        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];
            if (quoted)
            {
                if (c == '"') quoted = false;
                else if (c == '\\' && i + 1 < exec.Length && exec[i + 1] is '"' or '`' or '$' or '\\') current.Append(exec[++i]);
                else current.Append(c);
            }
            else if (c == '"')
            {
                quoted = true;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken) args.Add(current.ToString());
                current.Clear();
                inToken = false;
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }
        if (quoted) return null;
        if (inToken) args.Add(current.ToString());
        return args;
    }

    // String values escape \s \n \t \r \\ (applied before the Exec quoting rules).
    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            var next = value[++i];
            sb.Append(next switch
            {
                's' => " ",
                'n' => "\n",
                't' => "\t",
                'r' => "\r",
                '\\' => "\\",
                _ => "\\" + next,
            });
        }
        return sb.ToString();
    }

    private static bool IsTrue(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);
}
