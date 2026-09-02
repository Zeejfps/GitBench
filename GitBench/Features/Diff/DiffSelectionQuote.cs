using System.Text;
using GitBench.Features.CodeIntel;
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
/// it came from, the declaration it sits in, the lines it covers, and which side of the change it is.
/// </summary>
/// <remarks>
/// The text comes from <see cref="DiffSelectionModel.BuildCopyText"/> — the same function the
/// clipboard uses — so what the assistant is shown is exactly what Ctrl+C would have produced,
/// gutters and +/- markers already stripped.
/// </remarks>
/// <param name="Declaration">The declaration the selection starts in, as a dotted containment path,
/// or null where the file did not parse. A line number tells the model where to look; a name tells it
/// what it is looking at, which is what a question is usually about.</param>
internal sealed record DiffSelectionQuote(
    string Path,
    FileLine? StartLine,
    FileLine? EndLine,
    DiffQuoteSide Side,
    string Text,
    string? Declaration = null)
{
    /// <summary>The quote for a selection, or null when it covers no code lines.</summary>
    public static DiffSelectionQuote? Build(
        IReadOnlyList<DiffRow> rows,
        DiffTextPos start,
        DiffTextPos end,
        string path,
        DiffAnnotations? annotations = null)
    {
        // No fold re-inflation here, and none needed: the quote path is reachable only where
        // DiffContentView.AssistantActions is set, which the file browser's preview — the one
        // surface that folds — deliberately leaves false. The day folding reaches the diff pane,
        // this call and the line-range loop below it both have to learn about hidden rows, or the
        // model gets re-inflated text with a range that does not describe it.
        var text = DiffSelectionModel.BuildCopyText(rows, start, end);
        if (text.Length == 0) return null;

        var added = false;
        var removed = false;
        var context = false;
        FileLine? first = null;
        FileLine? last = null;

        var lastRow = Math.Min(end.Row.Value, rows.Count - 1);
        for (var row = Math.Max(0, start.Row.Value); row <= lastRow; row++)
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
            var number = line.NewNumber.Line ?? line.OldNumber.Line;
            if (number is null) continue;
            first ??= number;
            last = number;
        }

        if (!added && !removed && !context) return null;

        var side = (added, removed, context) switch
        {
            (true, false, false) => DiffQuoteSide.Added,
            (false, true, false) => DiffQuoteSide.Removed,
            (false, false, true) => DiffQuoteSide.Context,
            _ => DiffQuoteSide.Mixed,
        };

        // Which outline names it follows the rule a hunk header follows: a selection of only
        // removed lines exists in the before-side file and nowhere else.
        var outline = removed && !added ? annotations?.OldSide : annotations?.NewSide;
        var declaration = first is { } firstLine ? outline?.DeclarationPathAt(firstLine.Value) : null;
        return new DiffSelectionQuote(path, first, last, side, text, declaration);
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
        if (StartLine is { } start)
        {
            var end = EndLine ?? start;
            builder.Append(", ");
            builder.Append(start == end
                ? $"line {start.Value}"
                : $"lines {start.Value}-{end.Value}");
        }

        if (Declaration is { Length: > 0 } declaration)
            builder.Append(", in `").Append(declaration).Append('`');

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
}
