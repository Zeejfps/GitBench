using System.Globalization;
using System.Text;
using GitBench.Git;

namespace GitBench.Features.Diff;

/// <summary>Which side of the diff a selection came off. A question about a line the change removed
/// means something different from one about a line it added, and a bare string loses that.</summary>
internal enum DiffQuoteSide
{
    Added,
    Removed,
    Context,
    Mixed,
}

/// <summary>
/// A selection in a diff, described well enough to ask a question about: the code itself, the file
/// it came from, the lines it covers, and which side of the change it is.
/// </summary>
/// <remarks>
/// The text comes from <see cref="DiffSelectionModel.BuildCopyText"/> — the same function the
/// clipboard uses — so what the assistant is shown is exactly what Ctrl+C would have produced,
/// gutters and +/- markers already stripped.
/// </remarks>
internal sealed record DiffSelectionQuote(
    string Path,
    int StartLine,
    int EndLine,
    DiffQuoteSide Side,
    string Text)
{
    /// <summary>The quote for a selection, or null when it covers no code lines.</summary>
    public static DiffSelectionQuote? Build(
        IReadOnlyList<DiffRow> rows,
        DiffTextPos start,
        DiffTextPos end,
        string path)
    {
        var text = DiffSelectionModel.BuildCopyText(rows, start, end);
        if (text.Length == 0) return null;

        var added = false;
        var removed = false;
        var context = false;
        var first = 0;
        var last = 0;

        var lastRow = Math.Min(end.Row, rows.Count - 1);
        for (var row = Math.Max(0, start.Row); row <= lastRow; row++)
        {
            if (rows[row] is not DiffRow.Line line) continue;

            switch (line.Kind)
            {
                case DiffLineKind.Added: added = true; break;
                case DiffLineKind.Removed: removed = true; break;
                default: context = true; break;
            }

            // The after-side number is what a reader cites; a removed line has only the before-side
            // one, so it stands in rather than leaving the range unnumbered.
            var number = Number(line.NewNumber) ?? Number(line.OldNumber);
            if (number is not { } value) continue;
            if (first == 0) first = value;
            last = value;
        }

        if (!added && !removed && !context) return null;

        var side = (added, removed, context) switch
        {
            (true, false, false) => DiffQuoteSide.Added,
            (false, true, false) => DiffQuoteSide.Removed,
            (false, false, true) => DiffQuoteSide.Context,
            _ => DiffQuoteSide.Mixed,
        };

        return new DiffSelectionQuote(path, first, last, side, text);
    }

    /// <summary>
    /// The selection as the model reads it: what it is, where it came from, then the code fenced.
    /// <paramref name="ask"/> leads when there is one — a preset's own question — and is omitted for
    /// the free-form case, where the person writes their own underneath.
    /// </summary>
    public string ToPrompt(string? ask)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(ask)) builder.Append(ask).Append("\n\n");

        builder.Append("Selected in the diff of `").Append(Path).Append('`');
        if (StartLine > 0)
        {
            builder.Append(", ");
            builder.Append(StartLine == EndLine
                ? $"line {StartLine}"
                : $"lines {StartLine}-{EndLine}");
        }

        builder.Append(" (").Append(SideName).Append("):\n\n```\n").Append(Text).Append("\n```");
        return builder.ToString();
    }

    private string SideName => Side switch
    {
        DiffQuoteSide.Added => "added lines",
        DiffQuoteSide.Removed => "removed lines",
        DiffQuoteSide.Context => "unchanged context lines",
        _ => "a mix of added, removed and context lines",
    };

    private static int? Number(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
