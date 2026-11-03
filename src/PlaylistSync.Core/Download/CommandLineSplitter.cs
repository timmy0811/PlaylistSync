using System.Text;

namespace PlaylistSync.Core.Download;

/// <summary>
/// Splits the free-text "extra yt-dlp arguments" setting into an argument list. No shell is involved:
/// whitespace separates arguments, single or double quotes group them, and inside double quotes
/// <c>\"</c> and <c>\\</c> are escapes. Backslashes elsewhere are literal, so Windows paths work unquoted.
/// </summary>
public static class CommandLineSplitter
{
    public static IReadOnlyList<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var current = new StringBuilder();
        var inArg = false;
        char? quote = null;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is null)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (inArg) result.Add(current.ToString());
                    current.Clear();
                    inArg = false;
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                    inArg = true;
                }
                else
                {
                    current.Append(c);
                    inArg = true;
                }
            }
            else if (c == quote)
            {
                quote = null;
            }
            else if (quote == '"' && c == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\')
            {
                current.Append(text[++i]);
            }
            else
            {
                current.Append(c);
            }
        }

        // An unterminated quote runs to the end of the text.
        if (inArg) result.Add(current.ToString());
        return result;
    }
}
