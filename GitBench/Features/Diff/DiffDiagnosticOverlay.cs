using GitBench.Lsp;

namespace GitBench.Features.Diff;

/// <summary>
/// One row's worth of a diagnostic: the part of the row it covers, in the tab-expanded column
/// space the painter draws in, and how bad it is.
/// </summary>
internal readonly record struct DiagnosticMark(CharRange Range, DiagnosticSeverity Severity);

/// <summary>
/// What the server said about a file, addressed the way the painter asks: by the line in front of
/// it. Held beside the rendered rows rather than inside them, because diagnostics arrive
/// repeatedly for minutes while the same rows stay on screen, and folding them in would mean
/// rebuilding every row on each wave.
/// </summary>
internal sealed class DiffDiagnosticOverlay
{
    public static readonly DiffDiagnosticOverlay Empty = new(string.Empty, []);

    private readonly Dictionary<int, List<Diagnostic>> _byLine = [];

    public DiffDiagnosticOverlay(string path, IReadOnlyList<Diagnostic> diagnostics)
    {
        Path = path;
        Count = diagnostics.Count;

        foreach (var diagnostic in diagnostics)
        {
            var (start, end) = Ordered(diagnostic.Range);
            for (var line = start.Line.Value; line <= end.Line.Value; line++)
            {
                if (!_byLine.TryGetValue(line, out var list)) _byLine[line] = list = [];
                list.Add(diagnostic);
            }

            var worst = Severity(diagnostic);
            if (worst < Worst) Worst = worst;
        }
    }

    public string Path { get; }

    public int Count { get; }

    public bool IsEmpty => Count == 0;

    public DiagnosticSeverity Worst { get; } = DiagnosticSeverity.Hint;

    /// <summary>The severity a whole row is marked with in the gutter, or null when nothing on the
    /// server's list touches it.</summary>
    public DiagnosticSeverity? SeverityOf(FileLine line)
    {
        if (!_byLine.TryGetValue(line.Value - 1, out var list)) return null;

        var worst = DiagnosticSeverity.Hint;
        foreach (var diagnostic in list)
        {
            var severity = Severity(diagnostic);
            if (severity < worst) worst = severity;
        }
        return worst;
    }

    /// <summary>
    /// The parts of one drawn line its diagnostics underline. A range reaching past the line is
    /// clipped to it, a range that ends at the start of a line covers the newline before rather
    /// than that line, and a range with no width at all is widened to one cell so that a server
    /// pointing between two characters still marks something.
    /// </summary>
    public IReadOnlyList<DiagnosticMark> MarksOn(FileLine line, DiffLineText text)
    {
        if (!_byLine.TryGetValue(line.Value - 1, out var list)) return [];

        var at = line.Value - 1;
        var marks = new List<DiagnosticMark>(list.Count);
        foreach (var diagnostic in list)
        {
            var (start, end) = Ordered(diagnostic.Range);
            if (at == end.Line.Value && at != start.Line.Value && end.Character.Value <= 0) continue;

            var from = at == start.Line.Value ? start.Character.Value : 0;
            var to = at == end.Line.Value ? end.Character.Value : text.Raw.Length;

            var left = text.ToExpanded(new RawColumn(from));
            var right = text.ToExpanded(new RawColumn(to));
            var width = Math.Max(1, right.Value - left.Value);
            marks.Add(new DiagnosticMark(new CharRange(left.Value, width), Severity(diagnostic)));
        }

        return marks;
    }

    /// <summary>Every diagnostic touching a line, for the card that shows what they say.</summary>
    public IReadOnlyList<Diagnostic> On(FileLine line) =>
        _byLine.TryGetValue(line.Value - 1, out var list) ? list : [];

    private static DiagnosticSeverity Severity(Diagnostic diagnostic) =>
        diagnostic.Severity == DiagnosticSeverity.Unspecified
            ? DiagnosticSeverity.Error
            : diagnostic.Severity;

    private static (LspPosition Start, LspPosition End) Ordered(LspRange range)
    {
        var (start, end) = (range.Start, range.End);
        var backwards = end.Line.Value < start.Line.Value ||
            (end.Line.Value == start.Line.Value && end.Character.Value < start.Character.Value);
        return backwards ? (end, start) : (start, end);
    }
}
