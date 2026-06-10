namespace GitBench;

/// <summary>
/// Per-diff syntax-highlight result: the token spans for the old and new file, each as a flat
/// list indexed by 1-based source line. Removed diff rows read from the old side, added/context
/// rows from the new side — see <see cref="ForLine"/>. Either side may be null (pure add/delete,
/// or that side failed/was unsupported), in which case its lookups return empty.
/// </summary>
internal sealed class DiffHighlight
{
    private static readonly IReadOnlyList<TokenSpan> Empty = Array.Empty<TokenSpan>();

    private readonly IReadOnlyList<IReadOnlyList<TokenSpan>>? _oldLines;
    private readonly IReadOnlyList<IReadOnlyList<TokenSpan>>? _newLines;

    public DiffHighlight(
        IReadOnlyList<IReadOnlyList<TokenSpan>>? oldLines,
        IReadOnlyList<IReadOnlyList<TokenSpan>>? newLines)
    {
        _oldLines = oldLines;
        _newLines = newLines;
    }

    /// <summary>Spans for a rendered diff row: removed rows resolve against the old file, added
    /// and context rows against the new file. Returns empty when no spans apply.</summary>
    public IReadOnlyList<TokenSpan> ForLine(DiffLineKind kind, int? oldLineNumber, int? newLineNumber)
    {
        if (kind == DiffLineKind.Removed)
            return oldLineNumber is int o ? Lookup(_oldLines, o) : Empty;
        return newLineNumber is int n ? Lookup(_newLines, n) : Empty;
    }

    private static IReadOnlyList<TokenSpan> Lookup(IReadOnlyList<IReadOnlyList<TokenSpan>>? lines, int lineNumber)
    {
        if (lines == null) return Empty;
        var idx = lineNumber - 1; // diff line numbers are 1-based; the span lists are 0-based
        return idx >= 0 && idx < lines.Count ? lines[idx] : Empty;
    }
}
