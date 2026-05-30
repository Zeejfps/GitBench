using System.Text;
using ZGF.Gui;

namespace GitGui;

// Pre-wraps a filesystem path so it fits inside <maxWidth>. The framework's TextWrapper
// only breaks on spaces, and paths have none — so a long path renders as one over-wide
// line. This walks the string and inserts explicit `\n` after path separators (`/` or
// `\`) when the current line would exceed the width. Falls back to a character break
// only when a single segment is itself wider than the line.
internal static class PathWrap
{
    public static string Wrap(string path, TextStyle style, float maxWidth, ICanvas canvas)
    {
        if (string.IsNullOrEmpty(path) || maxWidth <= 0f) return path;
        if (canvas.MeasureTextWidth(path, style) <= maxWidth) return path;

        var sb = new StringBuilder(path.Length + 8);
        var lineStart = 0;
        var lastBreakAfter = -1; // index of the char *after* which we last saw a separator

        for (var i = 0; i < path.Length; i++)
        {
            var span = path.AsSpan(lineStart, i - lineStart + 1).ToString();
            if (canvas.MeasureTextWidth(span, style) > maxWidth)
            {
                // Prefer a separator break inside this over-wide segment; otherwise
                // hard-break at the previous char so something always advances.
                var breakAfter = lastBreakAfter > lineStart ? lastBreakAfter : i;
                if (breakAfter <= lineStart) breakAfter = lineStart + 1;
                sb.Append(path.AsSpan(lineStart, breakAfter - lineStart));
                sb.Append('\n');
                lineStart = breakAfter;
                lastBreakAfter = -1;
                // Re-test from the new lineStart at the same position i.
                i = lineStart - 1;
                continue;
            }
            if (path[i] == '\\' || path[i] == '/')
                lastBreakAfter = i + 1;
        }
        sb.Append(path.AsSpan(lineStart));
        return sb.ToString();
    }
}
