using System.Text;
using GitBench.Git;

namespace GitBench.Features.Diff.Reading;

/// <summary>
/// Renders a compiled plan back as diff text, for the model to check its own draft against.
/// </summary>
/// <remarks>
/// This is the same flattening the view does, in text: rows hidden are absent, folded runs are one
/// ellipsis, elided rows carry their shortened text. Showing the model the real result — rather
/// than trusting it to imagine one — is what makes a second pass worth taking.
/// </remarks>
internal static class ReadingPreview
{
    private const int MaxBytes = 16 * 1024;

    public static string Render(ReadingOverlay overlay)
    {
        var b = new StringBuilder();
        var index = overlay.Index;
        var ordinal = 0;

        for (var f = 0; f < index.Files.Count; f++)
        {
            var file = index.Files[f];
            var fileHeaderWritten = false;

            foreach (var hunk in file.Hunks)
            {
                var start = ordinal;
                ordinal += hunk.Lines.Count;
                if (!HunkHasContent(overlay, start, hunk.Lines.Count)) continue;

                if (!fileHeaderWritten)
                {
                    b.Append("=== ").Append(file.Path).Append('\n');
                    fileHeaderWritten = true;
                }
                b.Append("@@ -").Append(hunk.OldStart).Append(',').Append(hunk.OldLines)
                    .Append(" +").Append(hunk.NewStart).Append(',').Append(hunk.NewLines).Append(" @@\n");

                for (var l = 0; l < hunk.Lines.Count; l++)
                {
                    var row = start + l + 1;
                    var line = hunk.Lines[l];
                    if (overlay.FoldAt(row) is { } fold)
                    {
                        b.Append(ReadingRowIndex.Marker(fold.Kind))
                            .Append(fold.Indent)
                            .Append(ReadingElisionRule.Marker)
                            .Append('\n');
                        continue;
                    }
                    if (overlay.IsHidden(row)) continue;
                    b.Append(ReadingRowIndex.Marker(line.Kind))
                        .Append(overlay.ElidedText(row) ?? line.Text)
                        .Append('\n');
                }
            }

            if (!fileHeaderWritten && file.Hunks.Count > 0)
                b.Append("=== ").Append(file.Path).Append(" (all hidden)\n");
        }

        return Truncate(b.ToString());
    }

    /// <summary>
    /// The one-line manifest of how much survived, counted from the source rather than reported by
    /// the model — so the reader always knows how much they are not reading.
    /// </summary>
    public static string ElisionLine(ReadingStats stats)
    {
        if (stats.RawChanged == 0) return string.Empty;
        if (stats.VisibleChanged == 0)
            return $"hid all {stats.RawChanged} changed lines in {stats.RawFiles} files";
        return $"kept {stats.VisibleChanged}/{stats.RawChanged} changed lines in {stats.VisibleFiles}/{stats.RawFiles} files";
    }

    /// <summary>Whether a plan is still carrying so much of the diff that another pass is worth
    /// asking for. Advisory: a dense change legitimately keeps most of its rows.</summary>
    public static bool RetentionIsHigh(ReadingStats stats)
    {
        if (stats.RawChanged < 40 || stats.VisibleChanged < 20) return false;
        return stats.VisibleChanged >= 80 || stats.RetainedPercent >= 45;
    }

    private static bool HunkHasContent(ReadingOverlay overlay, int start, int count)
    {
        for (var l = 0; l < count; l++)
        {
            var row = start + l + 1;
            if (!overlay.IsHidden(row) || overlay.FoldAt(row) != null) return true;
        }
        return false;
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaxBytes) return text;
        return text[..MaxBytes] + $"\n… (preview truncated at {MaxBytes} characters)";
    }
}
