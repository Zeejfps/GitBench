namespace GitBench.Features.Diff.Reading;

/// <summary>
/// The rule that keeps a within-line elision honest: the replacement must be the original with
/// spans deleted, each deletion left visible as an ellipsis.
/// </summary>
/// <remarks>
/// This is the whole anti-invention story for <see cref="ReadingElision"/>. Characters may only
/// be dropped — never added, reordered or altered — so an elided line cannot say anything the
/// source did not, however the text was produced.
/// </remarks>
internal static class ReadingElisionRule
{
    public const string Marker = "…";

    /// <summary>Whether <paramref name="replacement"/> is a valid elision of <paramref name="original"/>.</summary>
    public static bool IsProjection(string original, string replacement)
    {
        var segments = Split(replacement, out var markers);
        if (markers == 0) return false;

        var visited = new bool[segments.Count + 1, original.Length + 1];
        var failed = new bool[segments.Count + 1, original.Length + 1];
        return Match(original, segments, 0, 0, visited, failed);
    }

    /// <summary>
    /// Applies the elision to the one occurrence of <paramref name="original"/> in
    /// <paramref name="text"/>, or returns null when it does not occur exactly once.
    /// </summary>
    public static string? Apply(string text, string original, string replacement)
    {
        var first = text.IndexOf(original, StringComparison.Ordinal);
        if (first < 0) return null;
        if (text.IndexOf(original, first + 1, StringComparison.Ordinal) >= 0) return null;
        return string.Concat(text.AsSpan(0, first), replacement, text.AsSpan(first + original.Length));
    }

    private static bool Match(
        string original,
        List<string> segments,
        int segment,
        int position,
        bool[,] visited,
        bool[,] failed)
    {
        if (visited[segment, position]) return !failed[segment, position];
        visited[segment, position] = true;

        bool result;
        if (segment == segments.Count)
        {
            result = position == original.Length;
        }
        else
        {
            var text = segments[segment];
            if (segment == 0)
            {
                result = original.AsSpan(position).StartsWith(text, StringComparison.Ordinal)
                         && Match(original, segments, 1, position + text.Length, visited, failed);
            }
            else if (segment == segments.Count - 1 && text.Length == 0)
            {
                // A trailing ellipsis deletes through the end, so at least one character must remain.
                result = position < original.Length;
            }
            else
            {
                result = false;
                // Every deletion is non-empty, so a later segment starts strictly after the last one.
                var from = position + 1;
                while (from + text.Length <= original.Length)
                {
                    var at = original.IndexOf(text, from, StringComparison.Ordinal);
                    if (at < 0) break;
                    if (Match(original, segments, segment + 1, at + text.Length, visited, failed))
                    {
                        result = true;
                        break;
                    }
                    from = at + 1;
                }
            }
        }

        failed[segment, position] = !result;
        return result;
    }

    // Splits on ellipsis runs: the unicode character, or three or more dots.
    private static List<string> Split(string text, out int markers)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        markers = 0;
        var i = 0;
        while (i < text.Length)
        {
            var length = MarkerLengthAt(text, i);
            if (length > 0)
            {
                segments.Add(current.ToString());
                current.Clear();
                markers++;
                i += length;
                continue;
            }
            current.Append(text[i]);
            i++;
        }
        segments.Add(current.ToString());
        return segments;
    }

    private static int MarkerLengthAt(string text, int i)
    {
        if (text[i] == '…') return 1;
        if (text[i] != '.') return 0;
        var dots = 0;
        while (i + dots < text.Length && text[i + dots] == '.') dots++;
        return dots >= 3 ? dots : 0;
    }
}
