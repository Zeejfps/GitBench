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
}
