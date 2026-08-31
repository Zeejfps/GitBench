using GitBench.Features.CodeIntel;
using GitBench.Git;
using GitBench.Localization;

namespace GitBench.Features.Diff;

/// <summary>The inclusive flattened-row range one hunk occupies within a <see cref="DiffRowSet"/>.</summary>
internal sealed record HunkRowRange(int HunkIndex, int FirstRow, int LastRow);

/// <summary>
/// The flattened, draw-ready row stream of one diff render: banners, hunk separators / gap
/// chrome, and line rows, plus the per-row hunk map and the sizing facts (max visual cells,
/// gutter digits) horizontal extents derive from. Built once per render state; consumed by
/// <see cref="DiffContentView"/> (the single-file pane) and the review window's stacked list,
/// so both flatten a diff identically.
/// </summary>
internal sealed class DiffRowSet
{
    public static readonly DiffRowSet Empty = new();

    private readonly List<DiffRow> _rows = new();
    private readonly List<HunkRowRange> _hunkRanges = new();
    private readonly Dictionary<int, string> _hiddenAfter = new();
    private int[] _rowToHunk = Array.Empty<int>();
    private ILocalizationService _loc = null!;

    public IReadOnlyList<DiffRow> Rows => _rows;
    public IReadOnlyList<HunkRowRange> HunkRanges => _hunkRanges;
    public int MaxRowCells { get; private set; }

    // MaxRowCells is in monospace cells, but a separator bar also spends fixed pixels the painter
    // owns: the padding at each end and the gap either side of the header, plus the expander
    // glyph column when the bar carries one. Approximated in cells here — the same allowance
    // DiffRow.Tear makes — so a long derived header stays reachable at full horizontal scroll
    // instead of being truncated by DiffRowPainter.FitHeader.
    private const int SeparatorChromeCells = 6;
    private const int ExpanderColumnCells = 4;

    // Full-file mode draws a single (new-side) line-number gutter and no hunk chrome. Diff mode
    // leaves this false and renders the old|new two-gutter layout.
    public bool SingleGutter { get; private set; }

    /// <summary>Whether rows reserve the fold chevron column. Decided by the surface, not by whether
    /// anything actually folds: the outline arrives on a background lane, and a column that appeared
    /// when it landed would jog every line of text sideways a beat after the file opened.</summary>
    public bool FoldColumn { get; private set; }

    /// <summary>The text a collapsed fold swallowed after the given row, newline-joined, or null
    /// when that row hides nothing. Copying across a fold re-inflates from this — text the reader
    /// could not see is still text they selected.</summary>
    public string? HiddenAfter(int rowIndex) =>
        _hiddenAfter.TryGetValue(rowIndex, out var text) ? text : null;

    /// <summary>Max line-number digit count across the gutters (at least 1), for gutter width sizing.</summary>
    public int GutterDigits { get; private set; } = 1;

    /// <summary>The hunk owning a flattened row, or -1 for chrome rows (banners, separators, expanded context).</summary>
    public int HunkIndexOf(int rowIndex) =>
        rowIndex >= 0 && rowIndex < _rowToHunk.Length ? _rowToHunk[rowIndex] : -1;

    /// <summary>
    /// Flattens a render state into rows. <see cref="DiffRenderState.Loaded"/> and
    /// <see cref="DiffRenderState.FullFile"/> produce rows; every other state (and the loaded
    /// error/binary cases, which the hosts draw as centered placeholders) produces an empty set.
    /// </summary>
    public static DiffRowSet Build(DiffRenderState state, ILocalizationService loc, FoldState? folds = null)
    {
        var set = new DiffRowSet { _loc = loc };
        switch (state)
        {
            case DiffRenderState.Loaded loaded:
                set.FlattenRows(loaded.Result, loaded.Annotations, loaded.Expansion);
                break;
            case DiffRenderState.FullFile fullFile:
                set.FlattenFullFile(fullFile, folds);
                break;
        }
        return set;
    }

    private void FlattenRows(DiffResult r, DiffAnnotations? annotations, ContextExpansion? expansion)
    {
        if (r.ErrorMessage != null) return;
        if (r.IsBinary) return;
        if (r.Hunks.Count == 0 && !r.IsModeOnly && r.OldPath == null) return;

        AddChangeBanners(r);

        var gaps = DiffGaps.Compute(r, expansion?.Lines.Count);
        var totalLines = 0;
        for (var i = 0; i < r.Hunks.Count; i++)
            totalLines += EmitHunk(r, i, gaps[i], expansion, annotations);

        EmitEofGap(r, gaps[^1], expansion, annotations?.Highlight);

        if (r.Truncated)
            AddBanner(_loc.Strings.Value.DiffDiffTruncated(totalLines));

        FinalizeGutterAndHunkMap();
    }

    private void AddChangeBanners(DiffResult r)
    {
        var s = _loc.Strings.Value;
        if (r.OldPath != null)
            AddBanner(s.DiffRenamed(r.OldPath, r.Path));
        if (r.IsModeOnly)
            AddBanner(s.DiffModeChanged(FormatMode(r.OldMode), FormatMode(r.NewMode)));
    }

    // Emits one hunk (its leading gap chrome, revealed context, and diff lines) and returns the
    // hunk's line count for the truncation total.
    private int EmitHunk(DiffResult r, int hunkIndex, DiffGap gap, ContextExpansion? expansion, DiffAnnotations? annotations)
    {
        var h = r.Hunks[hunkIndex];
        var highlight = annotations?.Highlight;
        var (top, bottom, remaining) = GapState(gap, expansion);

        if (top > 0)
            EmitExpandedRows(gap.NewStart, gap.NewStart + top - 1, gap.OldNewDelta, expansion!, highlight);

        var barRowIndex = EmitGapSeparator(h, gap, top, bottom, remaining, annotations);

        if (bottom > 0)
            EmitExpandedRows(gap.NewEnd - bottom + 1, gap.NewEnd, gap.OldNewDelta, expansion!, highlight);

        // Rows revealed below the bar sit between it and the hunk, so the hover/button range
        // anchors on the bar only while the two are still adjacent.
        var firstHunkRow = barRowIndex >= 0 && bottom == 0 ? barRowIndex : _rows.Count;

        EmitHunkLines(h, highlight);
        _hunkRanges.Add(new HunkRowRange(hunkIndex, firstHunkRow, _rows.Count - 1));
        return h.Lines.Count;
    }

    // While lines stay hidden the gap keeps its chrome: a large middle gap splits into a
    // down-arrow bar hugging the hunk above, a torn "hidden lines" break, and an up-arrow bar
    // carrying the @@ header — each arrow pointing into the tear it reveals. Small and top-of-file
    // gaps stay a single bar, an untouched empty gap keeps the plain separator, and a fully
    // expanded gap drops everything so the hunks read as one continuous block. Returns the row
    // index of the header bar, or -1 when no separator is emitted.
    private int EmitGapSeparator(DiffHunk h, DiffGap gap, int top, int bottom, int? remaining, DiffAnnotations? annotations)
    {
        if (!(remaining > 0 || (top == 0 && bottom == 0)))
            return -1;

        var s = _loc.Strings.Value;
        var range = $"@@ -{h.OldStart},{h.OldLines} +{h.NewStart},{h.NewLines} @@";
        // The parsed declaration when the outlines can name one; otherwise git's own xfuncname
        // guess, which is also all a zero-line (truncated-away) hunk can offer.
        var header = annotations?.HunkHeader(h) ?? (string.IsNullOrEmpty(h.Header) ? null : h.Header);
        var sepCells = DiffText.VisualCells(range)
            + (header != null ? DiffText.VisualCells(header) : 0)
            + SeparatorChromeCells;
        int barRowIndex;
        if (remaining is int hidden && gap.GapIndex > 0 && hidden > DiffOptions.ContextExpandStep)
        {
            _rows.Add(new DiffRow.HunkSeparator(string.Empty, null,
                new GapBar(gap.GapIndex, ShowDown: true, ShowUp: false, ShowUnfold: false, HiddenCount: null)));
            _rows.Add(new DiffRow.Tear(
                new GapBar(gap.GapIndex, ShowDown: false, ShowUp: false, ShowUnfold: true, HiddenCount: hidden)));
            barRowIndex = _rows.Count;
            _rows.Add(new DiffRow.HunkSeparator(range, header,
                new GapBar(gap.GapIndex, ShowDown: false, ShowUp: true, ShowUnfold: false, HiddenCount: null)));
            sepCells += ExpanderColumnCells;
            var tearCells = DiffText.VisualCells(s.DiffHiddenLines(hidden)) + 10;
            if (tearCells > MaxRowCells) MaxRowCells = tearCells;
        }
        else
        {
            barRowIndex = _rows.Count;
            _rows.Add(new DiffRow.HunkSeparator(range, header,
                remaining > 0 ? BarFor(gap.GapIndex, remaining.Value) : null));
            if (remaining > 0)
                sepCells += ExpanderColumnCells + DiffText.VisualCells(s.DiffHiddenLines(remaining.Value)) + 2;
        }
        if (sepCells > MaxRowCells) MaxRowCells = sepCells;
        return barRowIndex;
    }

    private void EmitHunkLines(DiffHunk h, DiffHighlight? highlight)
    {
        // Tab-expand once: each row needs it for Text, and emphasis is computed in the same
        // tab-expanded column space so it aligns with Spans and the glyph grid.
        var expanded = new string[h.Lines.Count];
        for (var j = 0; j < h.Lines.Count; j++)
            expanded[j] = DiffText.ExpandTabs(h.Lines[j].Text);
        var emphasis = DiffOptions.IntraLineHighlightingEnabled
            ? IntraLineDiff.ForHunk(h.Lines, expanded)
            : null;

        for (var j = 0; j < h.Lines.Count; j++)
        {
            var l = h.Lines[j];
            var text = expanded[j];
            // Spans are produced over tab-expanded text (same ExpandTabs), so columns align.
            var spans = highlight?.ForLine(l.Kind, l.OldLineNumber, l.NewLineNumber);
            if (spans != null && spans.Count == 0) spans = null;
            _rows.Add(new DiffRow.Line(
                l.Kind,
                l.OldLineNumber?.ToString() ?? string.Empty,
                l.NewLineNumber?.ToString() ?? string.Empty,
                text,
                spans,
                emphasis?[j]));
            var cells = DiffText.VisualCells(text);
            if (cells > MaxRowCells) MaxRowCells = cells;
        }
    }

    // The EOF gap: expanded rows grow downward from the last hunk; the trailing bar shows while
    // lines remain below (before the fetch the count is unknown and the trailing-context heuristic
    // decides optimistically — the first click's re-flatten corrects it).
    private void EmitEofGap(DiffResult r, DiffGap eof, ContextExpansion? expansion, DiffHighlight? highlight)
    {
        var (eofTop, _, eofRemaining) = GapState(eof, expansion);
        if (eofTop > 0)
            EmitExpandedRows(eof.NewStart, eof.NewStart + eofTop - 1, eof.OldNewDelta, expansion!, highlight);

        var showEofBar = eofRemaining is int rem ? rem > 0 : !DiffGaps.LastHunkReachesEof(r);
        if (!showEofBar) return;

        _rows.Add(new DiffRow.HunkSeparator(string.Empty, null,
            new GapBar(eof.GapIndex, ShowDown: true, ShowUp: false, ShowUnfold: false, HiddenCount: eofRemaining)));
        if (eofRemaining is int n)
        {
            var eofCells = DiffText.VisualCells(_loc.Strings.Value.DiffHiddenLines(n)) + 2;
            if (eofCells > MaxRowCells) MaxRowCells = eofCells;
        }
    }

    // Gutter digits from the max line-number length, sized after emission so expanded rows'
    // (possibly larger) numbers are included; then map every row to its owning hunk (-1 for chrome).
    private void FinalizeGutterAndHunkMap()
    {
        var maxDigits = 1;
        foreach (var row in _rows)
        {
            if (row is DiffRow.Line l)
            {
                if (l.OldNumber.Length > maxDigits) maxDigits = l.OldNumber.Length;
                if (l.NewNumber.Length > maxDigits) maxDigits = l.NewNumber.Length;
            }
        }
        GutterDigits = maxDigits;

        _rowToHunk = new int[_rows.Count];
        Array.Fill(_rowToHunk, -1);
        foreach (var range in _hunkRanges)
            for (var i = range.FirstRow; i <= range.LastRow; i++)
                _rowToHunk[i] = range.HunkIndex;
    }

    // Revealed top/bottom counts and the remaining hidden count for a gap. Remaining is null
    // while the gap is open-ended (the EOF gap before any expansion exists).
    private static (int Top, int Bottom, int? Remaining) GapState(DiffGap gap, ContextExpansion? expansion)
    {
        if (gap.Count is not int total) return (0, 0, null);
        var shown = expansion != null && expansion.Gaps.TryGetValue(gap.GapIndex, out var g) ? g : null;
        var top = Math.Min(shown?.Top ?? 0, total);
        var bottom = Math.Min(shown?.Bottom ?? 0, total - top);
        return (top, bottom, total - top - bottom);
    }

    // Single-bar gaps: the top-of-file gap keeps a lone up arrow (it can only grow upward from
    // hunk 0), a small middle gap collapses to one unfold-all icon. Large middle gaps never get
    // here — they split into the bar/tear/bar arrangement instead.
    private static GapBar BarFor(int gapIndex, int remaining)
    {
        var unfold = gapIndex > 0;
        return new GapBar(gapIndex, ShowDown: false, ShowUp: !unfold, ShowUnfold: unfold, HiddenCount: remaining);
    }

    // Ordinary context rows for expanded gap lines [from..to] (1-based new-file numbers), the
    // old-side number recovered via the gap's delta. Emitted outside every hunk range, so
    // the hunk map stays -1 for them and hunk hover outlines and buttons ignore them.
    private void EmitExpandedRows(int from, int to, int oldNewDelta, ContextExpansion expansion, DiffHighlight? highlight)
    {
        for (var n = from; n <= to; n++)
        {
            if (n < 1 || n > expansion.Lines.Count) continue;
            var text = DiffText.ExpandTabs(expansion.Lines[n - 1]);
            // DiffHighlight tokenizes the whole new-side file, so spans exist beyond the hunks.
            var spans = highlight?.ForLine(DiffLineKind.Context, n + oldNewDelta, n);
            if (spans != null && spans.Count == 0) spans = null;
            _rows.Add(new DiffRow.Line(
                DiffLineKind.Context, (n + oldNewDelta).ToString(), n.ToString(), text, spans));
            var cells = DiffText.VisualCells(text);
            if (cells > MaxRowCells) MaxRowCells = cells;
        }
    }

    // Flattens the whole after-side file into one Line row per source line: lines in
    // AddedLineNumbers render as additions (tinted), the rest as context. Mirrors FlattenRows'
    // per-line formatting (tab expansion + new-side spans) so highlighting aligns identically,
    // but emits a single new-side gutter and no hunk separators.
    private void FlattenFullFile(DiffRenderState.FullFile ff, FoldState? folds)
    {
        SingleGutter = true;
        FoldColumn = folds != null;
        GutterDigits = Math.Max(1, DigitCount(ff.Lines.Count));

        var plan = FoldPlan.Build(ff, folds);
        var emphasis = ff.Emphasis;
        for (var i = 0; i < ff.Lines.Count; i++)
        {
            var lineNumber = i + 1;
            if (plan.IsHidden(lineNumber)) continue;

            var kind = ff.AddedLineNumbers.Contains(lineNumber) ? DiffLineKind.Added : DiffLineKind.Context;
            var text = DiffText.ExpandTabs(ff.Lines[i]);
            // Context kind drives ForLine to the new-side spans for every row (added or not),
            // which is exactly what the full after-side file needs.
            var spans = ff.Annotations?.Highlight?.ForLine(DiffLineKind.Context, null, lineNumber);
            if (spans != null && spans.Count == 0) spans = null;
            IReadOnlyList<CharRange>? em = null;
            emphasis?.TryGetValue(lineNumber, out em);
            var mark = plan.MarkAt(lineNumber);
            _rows.Add(new DiffRow.Line(kind, string.Empty, lineNumber.ToString(), text, spans, em, mark));

            var cells = DiffText.VisualCells(text);
            if (mark is { Chip: true }) cells += DiffText.VisualCells(FoldChipText);
            if (cells > MaxRowCells) MaxRowCells = cells;

            if (plan.SwallowedAt(lineNumber) is { } swallowed)
                _hiddenAfter[_rows.Count - 1] = swallowed;
        }

        if (ff.Truncated)
            AddBanner(_loc.Strings.Value.DiffFileTruncated(ff.Lines.Count));
    }

    /// <summary>What a collapsed fold leaves behind, appended to the declaration's own last line —
    /// the whole body including its braces, so a folded declaration reads as one line.</summary>
    public const string FoldChipText = "{...}";

    /// <summary>
    /// What the fold set means for one file's lines: which are hidden, which carry a chevron or a
    /// chip, and what text each collapsed fold swallowed. Resolved once per flatten so the line
    /// loop stays a loop over lines.
    /// </summary>
    /// <remarks>
    /// A collapsed declaration's children are never walked. Their rows are inside its hidden range,
    /// so a mark on one could not be seen and a nested hidden range could not add to the union —
    /// which is what "outermost wins" amounts to in practice.
    /// </remarks>
    private sealed class FoldPlan
    {
        private static readonly FoldPlan Nothing = new(0);

        private readonly bool[] _hidden;
        private readonly Dictionary<int, FoldMark> _marks = new();
        private readonly Dictionary<int, string> _swallowed = new();

        private FoldPlan(int lineCount) => _hidden = new bool[lineCount + 2];

        public static FoldPlan Build(DiffRenderState.FullFile ff, FoldState? folds)
        {
            if (folds is null || ff.Annotations?.NewSide is not { } outline) return Nothing;

            var plan = new FoldPlan(ff.Lines.Count);
            plan.Walk(outline.Roots, parentPath: null, folds, ff.Lines);
            return plan;
        }

        public bool IsHidden(int line) => line >= 0 && line < _hidden.Length && _hidden[line];

        public FoldMark? MarkAt(int line) => _marks.TryGetValue(line, out var mark) ? mark : null;

        public string? SwallowedAt(int line) => _swallowed.TryGetValue(line, out var text) ? text : null;

        private void Walk(
            IReadOnlyList<OutlineNode> nodes, string? parentPath, FoldState folds, IReadOnlyList<string> lines)
        {
            foreach (var node in nodes)
            {
                var path = FileOutline.PathOf(parentPath, node);

                // §4.1 sets SignatureEndLine to EndLine for anything declared without a body, so this
                // one comparison rules out expression-bodied members, interface members, abstract
                // methods, positional records, delegates and enum members alike.
                if (node.SignatureEndLine >= node.EndLine)
                {
                    Walk(node.Children, path, folds, lines);
                    continue;
                }

                var collapsed = folds.IsCollapsed(path);
                Mark(node.StartLine, path, collapsed, chevron: true, chip: false);
                if (!collapsed)
                {
                    Walk(node.Children, path, folds, lines);
                    continue;
                }

                // The body's opening brace goes with the body, so the chip lands on the last line
                // of the signature and the declaration collapses onto one row. Never onto the row
                // carrying the chevron's own start, which is what the Max guards: a signature and
                // its brace sometimes share a line.
                var hideFrom = Math.Max(node.StartLine + 1, node.SignatureEndLine);
                var chipLine = hideFrom - 1;
                var last = Math.Min(node.EndLine, lines.Count);
                if (last < hideFrom) continue;

                Mark(chipLine, path, collapsed: true, chevron: false, chip: true);
                for (var line = hideFrom; line <= last; line++)
                    _hidden[line] = true;
                _swallowed[chipLine] = Swallowed(lines, hideFrom, last);
            }
        }

        // A signature and its opening brace share a row in some styles, so the two marks merge
        // rather than one overwriting the other.
        private void Mark(int line, string path, bool collapsed, bool chevron, bool chip)
        {
            var existing = _marks.TryGetValue(line, out var m) ? m : new FoldMark(path, collapsed, false, false);
            _marks[line] = existing with
            {
                Id = path,
                Collapsed = collapsed,
                Chevron = existing.Chevron || chevron,
                Chip = existing.Chip || chip,
            };
        }

        // Tab-expanded like the visible rows, so re-inflated text lines up with what was copied
        // around it.
        private static string Swallowed(IReadOnlyList<string> lines, int from, int to)
        {
            var text = new System.Text.StringBuilder();
            for (var line = from; line <= to; line++)
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(DiffText.ExpandTabs(lines[line - 1]));
            }
            return text.ToString();
        }
    }

    private void AddBanner(string text)
    {
        _rows.Add(new DiffRow.Banner(text));
        var cells = DiffText.VisualCells(text);
        if (cells > MaxRowCells) MaxRowCells = cells;
    }

    private static int DigitCount(int n)
    {
        if (n <= 0) return 1;
        var d = 0;
        while (n > 0) { d++; n /= 10; }
        return d;
    }

    private static string FormatMode(int? mode)
        => mode is int m ? Convert.ToString(m, 8).PadLeft(6, '0') : "-";
}
