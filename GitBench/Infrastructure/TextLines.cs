namespace GitBench.Infrastructure;

/// <summary>The one place file text becomes display lines. A break is LF, CRLF or a lone CR.</summary>
internal static class TextLines
{
    /// <summary>A trailing break adds no empty last line.</summary>
    public static List<string> Split(string text, bool dropLastPartialLine = false)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n' && text[i] != '\r') continue;

            lines.Add(text[start..i]);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }

        if (start < text.Length && !dropLastPartialLine) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>Splits on '\n', tolerating '\r\n', and always keeps a final element so 1-based
    /// source line numbers index straight into the result (a file ending in a newline yields a
    /// trailing empty line, matching how the diff numbers its lines).</summary>
    public static List<string> SplitKeepingLast(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var end = i;
            if (end > start && text[end - 1] == '\r') end--;
            lines.Add(text[start..end]);
            start = i + 1;
        }
        lines.Add(text[start..]);
        return lines;
    }

    public static string NormalizeNewlines(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
}
