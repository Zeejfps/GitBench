using GitBench.Git;

namespace GitBench.Features.Diff.Reading;

/// <summary>The generated stand-in for a folded run: its marker, the indent it keeps, and how
/// many source rows it covers.</summary>
internal sealed record ReadingFoldRow(DiffLineKind Kind, string Indent, int HiddenCount, int StartRow, int EndRow);

/// <summary>How much of a diff survived abridging, counted from the source rather than reported.</summary>
internal readonly record struct ReadingStats(
    int RawChanged,
    int VisibleChanged,
    int RemovedChanged,
    int FoldedChanged,
    int FoldCount,
    int RawFiles,
    int VisibleFiles)
{
    public int RetainedPercent => RawChanged == 0 ? 0 : VisibleChanged * 100 / RawChanged;
}

/// <summary>
/// A compiled reading plan: which rows are hidden, which carry a generated fold row, and which
/// render with part of their text elided.
/// </summary>
/// <remarks>
/// Presentation state only. Like <see cref="ContextExpansion"/> it never touches the
/// <see cref="DiffResult"/> it describes, so staging, discarding and every other hunk operation
/// keep acting on the real diff while the reader looks at the abridged one.
/// </remarks>
internal sealed class ReadingOverlay
{
    private readonly ReadingRowIndex _index;
    private readonly bool[] _hidden;
    private readonly ReadingFoldRow?[] _foldAt;
    private readonly string?[] _elided;
    private readonly bool[] _fileEmpty;

    internal ReadingOverlay(
        ReadingRowIndex index,
        bool[] hidden,
        ReadingFoldRow?[] foldAt,
        string?[] elided,
        bool[] fileEmpty,
        ReadingStats stats,
        string? summary)
    {
        _index = index;
        _hidden = hidden;
        _foldAt = foldAt;
        _elided = elided;
        _fileEmpty = fileEmpty;
        Stats = stats;
        Summary = summary;
    }

    public ReadingRowIndex Index => _index;

    public ReadingStats Stats { get; }

    public string? Summary { get; }

    public bool IsHidden(int ordinal) => _hidden[ordinal - 1];

    /// <summary>The fold row emitted in place of this source row, or null when none starts here.</summary>
    public ReadingFoldRow? FoldAt(int ordinal) => _foldAt[ordinal - 1];

    public string? ElidedText(int ordinal) => _elided[ordinal - 1];

    /// <summary>Whether every changed row of a file was hidden, so the reader can be shown one
    /// collapsed strip instead of an empty file card.</summary>
    public bool FileIsFullyHidden(int fileIndex) => _fileEmpty[fileIndex];

    /// <summary>
    /// The planned file matching this one, or -1 when the plan does not describe it.
    /// </summary>
    /// <remarks>
    /// Matching is by path and then by shape — the same hunk count with the same line counts. The
    /// panes load their own <see cref="DiffResult"/> instances, so reference equality would never
    /// hit; the shape check is what stops a plan being applied to a diff that has since changed
    /// underneath it, where every coordinate would land on the wrong row. A mismatch renders the
    /// raw diff, which is always a safe answer.
    /// </remarks>
    public int IndexOfFile(DiffResult file)
    {
        for (var i = 0; i < _index.Files.Count; i++)
        {
            var planned = _index.Files[i];
            if (ReferenceEquals(planned, file)) return i;
            if (!string.Equals(planned.Path, file.Path, StringComparison.Ordinal)) continue;
            return SameShape(planned, file) ? i : -1;
        }
        return -1;
    }

    private static bool SameShape(DiffResult a, DiffResult b)
    {
        if (a.Hunks.Count != b.Hunks.Count) return false;
        for (var h = 0; h < a.Hunks.Count; h++)
            if (a.Hunks[h].Lines.Count != b.Hunks[h].Lines.Count) return false;
        return true;
    }

    /// <summary>The overlay narrowed to one file, in the coordinates the row flattener walks.</summary>
    public ReadingFileOverlay? ForFile(DiffResult file)
    {
        var at = IndexOfFile(file);
        return at < 0 ? null : new ReadingFileOverlay(this, at);
    }
}

/// <summary>One file's slice of a <see cref="ReadingOverlay"/>, addressed by hunk and line.</summary>
internal readonly struct ReadingFileOverlay
{
    private readonly ReadingOverlay _overlay;
    private readonly int _fileIndex;

    internal ReadingFileOverlay(ReadingOverlay overlay, int fileIndex)
    {
        _overlay = overlay;
        _fileIndex = fileIndex;
    }

    public bool IsFullyHidden => _overlay.FileIsFullyHidden(_fileIndex);

    public bool IsHidden(int hunkIndex, int lineIndex)
    {
        var ordinal = _overlay.Index.OrdinalOf(_fileIndex, hunkIndex, lineIndex);
        return ordinal != 0 && _overlay.IsHidden(ordinal);
    }

    public ReadingFoldRow? FoldAt(int hunkIndex, int lineIndex)
    {
        var ordinal = _overlay.Index.OrdinalOf(_fileIndex, hunkIndex, lineIndex);
        return ordinal == 0 ? null : _overlay.FoldAt(ordinal);
    }

    public string? ElidedText(int hunkIndex, int lineIndex)
    {
        var ordinal = _overlay.Index.OrdinalOf(_fileIndex, hunkIndex, lineIndex);
        return ordinal == 0 ? null : _overlay.ElidedText(ordinal);
    }

    /// <summary>Whether a hunk has anything left to draw, so a hunk emptied by the plan can drop
    /// its separator instead of leaving orphaned chrome.</summary>
    public bool HunkHasVisibleRows(DiffResult file, int hunkIndex)
    {
        var lines = file.Hunks[hunkIndex].Lines;
        for (var l = 0; l < lines.Count; l++)
        {
            var ordinal = _overlay.Index.OrdinalOf(_fileIndex, hunkIndex, l);
            if (ordinal == 0) continue;
            if (!_overlay.IsHidden(ordinal) || _overlay.FoldAt(ordinal) != null) return true;
        }
        return false;
    }
}
