using GitBench.Controls;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Fonts;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.VirtualRowList;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Diff;

/// <summary>
/// Virtualized diff body. Vertical scroll, hit-test boilerplate, and visible-row culling
/// live in a child <see cref="VirtualRowListView"/>; this view keeps horizontal scroll
/// (the widget is vertical-only), font-metric resolution, and per-row drawing. Emits
/// normalized scroll-position and scale updates on both axes so an external scrollbar
/// sync controller can drive the scrollbars.
/// </summary>
internal enum HunkAction { None, Stage, Unstage, Discard }

internal sealed class DiffContentView : View, IScrollableContent
{
    private const float GlyphColumnWidth = 18f;
    private const float BannerPaddingX = 8f;
    private const float HunkHeaderGap = 12f;
    private const float AssumedFontSize = FontSize.Body;
    // Fallback mono advance ratio if the canvas isn't available yet to measure a glyph.
    private const float FallbackMonoAdvanceRatio = 0.6f;

    private const float HunkButtonHeight = 18f;
    private const float HunkButtonPaddingX = 10f;
    private const float HunkButtonGap = 6f;
    private const float HunkButtonsMarginRight = 8f;
    private const float HunkButtonsTopInset = 4f;
    private const float HunkButtonFontSize = FontSize.Caption;
    private const float HunkOutlineThickness = 1f;

    // Gap-expander icons on the separator bars: fixed-width clickable cells at the far left of
    // the bar, over the gutter columns. Draw and hit-test share this geometry.
    private const float ExpanderPadLeft = 4f;
    private const float ExpanderCellWidth = 22f;
    private const float ExpanderChipGap = 2f;
    private const float ExpanderChipInsetY = 1.5f;

    // Shared style instances. TextStyle is a class so DrawTextInputs holds a reference; we
    // mutate the few that need per-row recoloring (banner/glyph/line text in the row body)
    // on the UI thread between draw calls, so there's no aliasing concern.
    // The code grid is pinned LTR (BaseDirection.Ltr): source lines and the line-number gutter are
    // a fixed left-origin monospace grid, so they must not follow the UI direction or right-align /
    // bidi-reorder when the locale is RTL — only the surrounding chrome mirrors.
    private static readonly TextStyle MonoMetricsStyle = new()
    {
        FontFamily = DiffOptions.MonoFontFamily,
        FontSize = AssumedFontSize,
        BaseDirection = BidiDirection.Ltr,
    };
    private static readonly TextStyle MonoStartStyle = new()
    {
        FontFamily = DiffOptions.MonoFontFamily,
        FontSize = AssumedFontSize,
        VerticalAlignment = TextAlignment.Center,
        BaseDirection = BidiDirection.Ltr,
    };
    private static readonly TextStyle MonoEndStyle = new()
    {
        FontFamily = DiffOptions.MonoFontFamily,
        FontSize = AssumedFontSize,
        HorizontalAlignment = TextAlignment.End,
        VerticalAlignment = TextAlignment.Center,
        BaseDirection = BidiDirection.Ltr,
    };
    private static readonly TextStyle MonoCenterStyle = new()
    {
        FontFamily = DiffOptions.MonoFontFamily,
        FontSize = AssumedFontSize,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
        BaseDirection = BidiDirection.Ltr,
    };
    private static readonly TextStyle PlaceholderStyle = new()
    {
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };
    private static readonly TextStyle HunkButtonTextStyle = new()
    {
        FontSize = HunkButtonFontSize,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };
    private static readonly TextStyle ExpanderIconStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Body,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };

    public event Action<float>? VerticalScrollPositionChanged;
    public event Action<float>? HorizontalScrollPositionChanged;

    public float VerticalScale { get; private set; } = 1f;
    public float HorizontalScale { get; private set; } = 1f;

    private DiffContentStyles _styles = ThemeStyles.Dark.DiffContent;
    private DiffHunkButtonStyles _buttonStyles = ThemeStyles.Dark.DiffHunkButton;

    private DiffRenderState _renderState = new DiffRenderState.Placeholder("Select a file to view diff.");
    private readonly List<DiffRow> _rows = new();
    private int _maxRowCells;
    private float _gutterWidth;
    private float _lineHeight;
    private float _monoAdvance;
    private bool _metricsResolved;

    private readonly List<HunkRowRange> _hunkRanges = new();
    private int[] _rowToHunk = Array.Empty<int>();
    private DiffSide _diffSide;
    private bool _hunksPatchable;
    // Full-file mode draws a single (new-side) line-number gutter and no hunk chrome. Diff mode
    // leaves this false and renders the old|new two-gutter layout pixel-identically to before.
    private bool _singleGutter;
    private int _hoveredHunkIndex = -1;
    private HunkAction _hoveredButton = HunkAction.None;
    private int _hoveredExpanderRow = -1;
    private GapExpandDirection _hoveredExpanderDir;
    private float _stageBtnTextWidth;
    private float _unstageBtnTextWidth;
    private float _discardBtnTextWidth;
    private bool _buttonMetricsResolved;

    public Action<int>? OnStageHunk { get; set; }
    public Action<int>? OnUnstageHunk { get; set; }
    public Action<int>? OnDiscardHunk { get; set; }
    public Action<int, GapExpandDirection>? OnExpandGap { get; set; }

    private readonly VirtualRowListView _list;
    private readonly ILocalizationService _loc;

    private float _scrollX;
    // A programmatic vertical scroll target that must be re-asserted across frames. Setting a
    // non-zero scroll right as content changes can be clobbered: when taller content makes the
    // vertical scrollbar transition hidden→visible, the bar's layout echoes a stale position
    // (0) back through the sync controller. We re-apply the target for a few frames until the
    // bar settles and the value sticks, then release control so the user can scroll freely.
    private float? _pendingScrollY;
    private int _pendingScrollFrames;
    private float _lastNormalizedY;
    private float _lastNormalizedX;
    // Sentinel start so the very first NotifyScrollChanged fires the event even when the
    // computed scale equals 1. The scrollbar thumb's built-in default is Scale=0.5 with
    // PreferredHeight=12 — without an explicit "scale=1, hide" message it stays visible
    // at half width until something else (a file that genuinely needs scroll) forces a
    // change. -1f is impossible for a real scale.
    private float _lastVerticalScale = -1f;
    private float _lastHorizontalScale = -1f;

    public DiffContentView(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        var theme = ctx.Theme();
        _loc = ctx.Localization();

        _list = new VirtualRowListView
        {
            RowHeight = AssumedFontSize, // placeholder until canvas-derived metrics resolve
            ItemBuilder = DrawDiffRowAt,
            ScrollWheelStep = Scrolling.WheelStep,
        };
        _list.ScrollChanged += () => NotifyScrollChanged(viewportFits: false);
        _list.HorizontalWheelHandler = OnHorizontalWheel;

        AddChildToSelf(_list);
        _list.UseController(input, () => new VirtualRowListController(_list));
        this.UseController(input, () => new DiffMouseController(this), EventPhaseFilter.Capture);

        this.BindThemed(theme, s =>
        {
            _styles = s.DiffContent;
            _buttonStyles = s.DiffHunkButton;
            SetDirty();
        });

        // Placeholder/conflict text is custom-painted, so repaint on a live language switch.
        // Hunk-button labels are measured and cached; drop the cache so they re-measure in the
        // new language on the next draw.
        this.Bind(_loc.Strings, _ => { _buttonMetricsResolved = false; SetDirty(); });
    }

    private void OnHorizontalWheel(float deltaX)
    {
        var prev = _scrollX;
        _scrollX -= deltaX * _list.ScrollWheelStep;
        ClampHorizontalScroll();
        if (_scrollX != prev)
        {
            SetDirty();
            NotifyScrollChanged(viewportFits: false);
        }
    }

    public void SetRenderState(DiffRenderState state)
    {
        // Capture the outgoing view's identity and position before rebuilding rows, so we can
        // preserve the reading position across a mode toggle and hold it across the async
        // highlight re-emit that follows. _renderState still holds the previous state here.
        var (prevPath, prevWasFullFile) = DescribeState(_renderState);
        var hadTopLine = TryGetTopVisibleNewLine(out var prevTopLine);
        var prevScrollY = _list.ScrollY;

        _renderState = state;
        _rows.Clear();
        _hunkRanges.Clear();
        _rowToHunk = Array.Empty<int>();
        _maxRowCells = 0;
        _scrollX = 0;
        _hoveredHunkIndex = -1;
        _hoveredButton = HunkAction.None;
        _hoveredExpanderRow = -1;
        _hunksPatchable = false;
        _singleGutter = false;
        _diffSide = DiffSide.Unstaged;
        // Metrics depend only on font, not content, but content width depends on metrics;
        // a fresh model forces a recompute on next draw.
        _metricsResolved = false;

        if (state is DiffRenderState.Loaded loaded)
        {
            _diffSide = loaded.Result.Side;
            _hunksPatchable = HunkPatchBuilder.CanPatchHunk(loaded.Result);
            FlattenRows(loaded.Result, loaded.Highlight, loaded.Expansion);
        }
        else if (state is DiffRenderState.FullFile fullFile)
        {
            // No hunk ranges/patchability: with _hunkRanges empty and _hunksPatchable false,
            // separators, outlines, and Stage/Discard buttons all suppress themselves.
            FlattenFullFile(fullFile);
        }

        _list.ItemCount = _rows.Count;
        _list.NotifyItemsChanged();
        ApplyScrollForTransition(state, prevPath, prevWasFullFile, hadTopLine, prevTopLine, prevScrollY);
        SetDirty();
    }

    // Lead-in rows kept above a "scroll to line" target so the line isn't flush against the top.
    private const int ScrollLeadIn = 3;

    // Chooses the scroll position for a freshly-built render: preserve the read line across a
    // toggle, hold the offset across same-state re-emits (highlight), or land on the first change
    // for a fresh full-file load. Falls back to the top — the prior behavior for plain diffs.
    private void ApplyScrollForTransition(
        DiffRenderState state, string? prevPath, bool prevWasFullFile,
        bool hadTopLine, int prevTopLine, float prevScrollY)
    {
        var (newPath, newIsFullFile) = DescribeState(state);

        if (newPath != null && newPath == prevPath)
        {
            // Same file. A flipped mode is a toggle → remap the top line into the new layout;
            // an unchanged mode is a re-emit (highlight attach, working-tree reload) → keep the
            // exact offset so neither the highlight nor a toggle's follow-up snaps to the top.
            if (newIsFullFile != prevWasFullFile && hadTopLine)
                ScrollToNewLine(prevTopLine, ScrollLeadIn);
            else
                SetScrollTarget(prevScrollY);
            return;
        }

        // Fresh full-file load for a different file: land on the first changed line with a little
        // context above it; fall back to the top when the file has no additions.
        if (newIsFullFile && state is DiffRenderState.FullFile ff && ff.AddedLineNumbers.Count > 0)
        {
            var first = int.MaxValue;
            foreach (var n in ff.AddedLineNumbers)
                if (n < first) first = n;
            ScrollToNewLine(first, ScrollLeadIn);
            return;
        }

        SetScrollTarget(0f);
    }

    private static (string? Path, bool IsFullFile) DescribeState(DiffRenderState state) => state switch
    {
        DiffRenderState.Loaded l => (l.Result.Path, false),
        DiffRenderState.FullFile ff => (ff.Path, true),
        _ => (null, false),
    };

    private void FlattenRows(DiffResult r, DiffHighlight? highlight, ContextExpansion? expansion)
    {
        if (r.ErrorMessage != null) return;
        if (r.IsBinary) return;
        if (r.Hunks.Count == 0 && !r.IsModeOnly && r.OldPath == null) return;

        var s = _loc.Strings.Value;
        if (r.OldPath != null)
            AddBanner(s.DiffRenamed(r.OldPath, r.Path));
        if (r.IsModeOnly)
            AddBanner(s.DiffModeChanged(FormatMode(r.OldMode), FormatMode(r.NewMode)));

        var gaps = DiffGaps.Compute(r, expansion?.Lines.Count);
        var totalLines = 0;

        for (var i = 0; i < r.Hunks.Count; i++)
        {
            var h = r.Hunks[i];
            var gap = gaps[i];
            var (top, bottom, remaining) = GapState(gap, expansion);

            if (top > 0)
                EmitExpandedRows(gap.NewStart, gap.NewStart + top - 1, gap.OldNewDelta, expansion!, highlight);

            // While lines stay hidden the gap keeps its chrome: a large middle gap splits into a
            // down-arrow bar hugging the hunk above, a torn "hidden lines" break, and an
            // up-arrow bar carrying the @@ header — each arrow pointing into the tear it
            // reveals. Small and top-of-file gaps stay a single bar, an untouched empty gap
            // keeps the plain separator, and a fully expanded gap drops everything so the hunks
            // read as one continuous block.
            var barRowIndex = -1;
            if (remaining > 0 || (top == 0 && bottom == 0))
            {
                var range = $"@@ -{h.OldStart},{h.OldLines} +{h.NewStart},{h.NewLines} @@";
                var header = string.IsNullOrEmpty(h.Header) ? null : h.Header;
                var sepCells = VisualCells(range) + (header != null ? VisualCells(header) : 0) + 2;
                if (remaining is int hidden && gap.GapIndex > 0 && hidden > DiffOptions.ContextExpandStep)
                {
                    _rows.Add(new DiffRow.HunkSeparator(string.Empty, null,
                        new GapBar(gap.GapIndex, ShowDown: true, ShowUp: false, ShowUnfold: false, HiddenCount: null)));
                    _rows.Add(new DiffRow.Tear(
                        new GapBar(gap.GapIndex, ShowDown: false, ShowUp: false, ShowUnfold: true, HiddenCount: hidden)));
                    barRowIndex = _rows.Count;
                    _rows.Add(new DiffRow.HunkSeparator(range, header,
                        new GapBar(gap.GapIndex, ShowDown: false, ShowUp: true, ShowUnfold: false, HiddenCount: null)));
                    var tearCells = VisualCells(s.DiffHiddenLines(hidden)) + 10;
                    if (tearCells > _maxRowCells) _maxRowCells = tearCells;
                }
                else
                {
                    barRowIndex = _rows.Count;
                    _rows.Add(new DiffRow.HunkSeparator(range, header,
                        remaining > 0 ? BarFor(gap.GapIndex, remaining.Value) : null));
                    if (remaining > 0)
                        sepCells += VisualCells(s.DiffHiddenLines(remaining.Value)) + 2;
                }
                if (sepCells > _maxRowCells) _maxRowCells = sepCells;
            }

            if (bottom > 0)
                EmitExpandedRows(gap.NewEnd - bottom + 1, gap.NewEnd, gap.OldNewDelta, expansion!, highlight);

            // Rows revealed below the bar sit between it and the hunk, so the hover/button
            // range anchors on the bar only while the two are still adjacent.
            var firstHunkRow = barRowIndex >= 0 && bottom == 0 ? barRowIndex : _rows.Count;

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
                var row = new DiffRow.Line(
                    l.Kind,
                    l.OldLineNumber?.ToString() ?? string.Empty,
                    l.NewLineNumber?.ToString() ?? string.Empty,
                    text,
                    text.Length,
                    spans,
                    emphasis?[j]);
                _rows.Add(row);
                var cells = VisualCells(text);
                if (cells > _maxRowCells) _maxRowCells = cells;
            }
            _hunkRanges.Add(new HunkRowRange(i, firstHunkRow, _rows.Count - 1));
            totalLines += h.Lines.Count;
        }

        // The EOF gap: expanded rows grow downward from the last hunk; the trailing bar shows
        // while lines remain below (before the fetch the count is unknown and the trailing-
        // context heuristic decides optimistically — the first click's re-flatten corrects it).
        var eof = gaps[^1];
        var (eofTop, _, eofRemaining) = GapState(eof, expansion);
        if (eofTop > 0)
            EmitExpandedRows(eof.NewStart, eof.NewStart + eofTop - 1, eof.OldNewDelta, expansion!, highlight);
        var showEofBar = eofRemaining is int rem ? rem > 0 : !DiffGaps.LastHunkReachesEof(r);
        if (showEofBar)
        {
            _rows.Add(new DiffRow.HunkSeparator(string.Empty, null,
                new GapBar(eof.GapIndex, ShowDown: true, ShowUp: false, ShowUnfold: false, HiddenCount: eofRemaining)));
            if (eofRemaining is int n)
            {
                var eofCells = VisualCells(s.DiffHiddenLines(n)) + 2;
                if (eofCells > _maxRowCells) _maxRowCells = eofCells;
            }
        }

        if (r.Truncated)
            AddBanner(s.DiffDiffTruncated(totalLines));

        // Gutter width from the max digit count, sized after emission so expanded rows'
        // (possibly larger) line numbers are included.
        _gutterWidth = Math.Max(1, GutterDigitCount()) * AssumedFontSize * FallbackMonoAdvanceRatio + 8f;

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
    // _rowToHunk stays -1 for them and hunk hover outlines and buttons ignore them.
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
                DiffLineKind.Context, (n + oldNewDelta).ToString(), n.ToString(), text, text.Length, spans));
            var cells = VisualCells(text);
            if (cells > _maxRowCells) _maxRowCells = cells;
        }
    }

    // Flattens the whole after-side file into one Line row per source line: lines in
    // AddedLineNumbers render as additions (tinted), the rest as context. Mirrors FlattenRows'
    // per-line formatting (tab expansion + new-side spans) so highlighting aligns identically,
    // but emits a single new-side gutter and no hunk separators.
    private void FlattenFullFile(DiffRenderState.FullFile ff)
    {
        _diffSide = ff.Side;
        _singleGutter = true;

        var digits = Math.Max(1, DigitCount(ff.Lines.Count));
        _gutterWidth = digits * AssumedFontSize * FallbackMonoAdvanceRatio + 8f;

        var emphasis = ff.Emphasis;
        for (var i = 0; i < ff.Lines.Count; i++)
        {
            var lineNumber = i + 1;
            var kind = ff.AddedLineNumbers.Contains(lineNumber) ? DiffLineKind.Added : DiffLineKind.Context;
            var text = DiffText.ExpandTabs(ff.Lines[i]);
            // Context kind drives ForLine to the new-side spans for every row (added or not),
            // which is exactly what the full after-side file needs.
            var spans = ff.Highlight?.ForLine(DiffLineKind.Context, null, lineNumber);
            if (spans != null && spans.Count == 0) spans = null;
            IReadOnlyList<CharRange>? em = null;
            emphasis?.TryGetValue(lineNumber, out em);
            _rows.Add(new DiffRow.Line(kind, string.Empty, lineNumber.ToString(), text, text.Length, spans, em));
            var cells = VisualCells(text);
            if (cells > _maxRowCells) _maxRowCells = cells;
        }

        if (ff.Truncated)
            AddBanner(_loc.Strings.Value.DiffFileTruncated(ff.Lines.Count));
    }

    private void AddBanner(string text)
    {
        _rows.Add(new DiffRow.Banner(text));
        var cells = VisualCells(text);
        if (cells > _maxRowCells) _maxRowCells = cells;
    }

    public void SetVerticalNormalizedScrollPosition(float normalized)
    {
        var range = ContentHeight() - Position.Height;
        if (range <= 0) { _list.SetScrollY(0f); }
        else { _list.SetScrollY(Math.Clamp(normalized, 0f, 1f) * range); }
    }

    public void SetHorizontalNormalizedScrollPosition(float normalized)
    {
        var range = ContentWidth() - Position.Width;
        if (range <= 0) { _scrollX = 0; }
        else { _scrollX = Math.Clamp(normalized, 0f, 1f) * range; }
        SetDirty();
    }

    // The new-file line number of the topmost visible row, used to preserve the reading position
    // across a Diff↔FullFile toggle. Skips banners/separators and removed rows (no new number).
    // Returns false before metrics resolve or when no row carries a new-side number.
    public bool TryGetTopVisibleNewLine(out int lineNumber)
    {
        lineNumber = 0;
        if (_lineHeight <= 0 || _rows.Count == 0) return false;
        var topIndex = Math.Clamp((int)(_list.ScrollY / _lineHeight), 0, _rows.Count - 1);
        for (var i = topIndex; i < _rows.Count; i++)
        {
            if (_rows[i] is DiffRow.Line l && l.NewNumber.Length > 0 && int.TryParse(l.NewNumber, out var n))
            {
                lineNumber = n;
                return true;
            }
        }
        return false;
    }

    // Scrolls so the row for the given new-file line sits leadIn rows below the top. No-op if the
    // line isn't present (e.g. a removed line that never had a new-side number).
    public void ScrollToNewLine(int lineNumber, int leadIn)
    {
        if (_lineHeight <= 0 || _rows.Count == 0) return;
        var rowIndex = FindRowForNewLine(lineNumber);
        if (rowIndex < 0) return;
        SetScrollTarget(Math.Max(0, rowIndex - leadIn) * _lineHeight);
    }

    // First row whose new-side line number equals lineNumber; falls back to the closest preceding
    // numbered row so a target with no exact row still lands sensibly. New numbers are monotonic
    // in row order in both modes, so a single forward scan suffices.
    private int FindRowForNewLine(int lineNumber)
    {
        var best = -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i] is not DiffRow.Line l || l.NewNumber.Length == 0) continue;
            if (!int.TryParse(l.NewNumber, out var n)) continue;
            if (n == lineNumber) return i;
            if (n < lineNumber) best = i;
        }
        return best;
    }

    private float ContentHeight()
    {
        if (_lineHeight <= 0) return 0f;
        return _rows.Count * _lineHeight;
    }

    private float ContentWidth()
    {
        // Always at least the viewport: short diffs shouldn't leave dead space on the right
        // where the colored row backgrounds would visibly stop short of the edge.
        var natural = ComputeNaturalContentWidth();
        return Math.Max(Position.Width, natural);
    }

    private float ComputeNaturalContentWidth()
    {
        if (_monoAdvance <= 0) return 0f;
        // Worst case across row kinds: line rows go gutter|gutter|glyph|text (one gutter in
        // full-file mode); banner rows are flush-left with horizontal padding. Take the max.
        var gutters = _singleGutter ? _gutterWidth : _gutterWidth + _gutterWidth;
        var lineWidth = gutters + GlyphColumnWidth + _maxRowCells * _monoAdvance + BannerPaddingX;
        var bannerWidth = BannerPaddingX * 2 + _maxRowCells * _monoAdvance;
        return Math.Max(lineWidth, bannerWidth);
    }

    // East-Asian display width in monospace cells: wide/fullwidth code points take two cells,
    // everything else one. The horizontal extent is sized from this so a spaceless CJK line —
    // whose glyphs run about twice a Latin cell — can be scrolled fully into view instead of
    // being cut off at the right edge. Two cells slightly over-estimates the fallback font's real
    // advance, which only ever leaves a little slack — it never clips.
    private static int VisualCells(string text)
    {
        var cells = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            int cp;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(c, text[i + 1]);
                i++;
            }
            else
            {
                cp = c;
            }

            cells += IsWideCodePoint(cp) ? 2 : 1;
        }

        return cells;
    }

    private static bool IsWideCodePoint(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F) ||   // Hangul Jamo
        (cp >= 0x2E80 && cp <= 0x303E) ||   // CJK Radicals … CJK Symbols and Punctuation
        (cp >= 0x3041 && cp <= 0x33FF) ||   // Hiragana … Enclosed CJK Letters and Months
        (cp >= 0x3400 && cp <= 0x4DBF) ||   // CJK Unified Ideographs Extension A
        (cp >= 0x4E00 && cp <= 0x9FFF) ||   // CJK Unified Ideographs
        (cp >= 0xA960 && cp <= 0xA97F) ||   // Hangul Jamo Extended-A
        (cp >= 0xAC00 && cp <= 0xD7A3) ||   // Hangul Syllables
        (cp >= 0xF900 && cp <= 0xFAFF) ||   // CJK Compatibility Ideographs
        (cp >= 0xFF00 && cp <= 0xFF60) ||   // Fullwidth Forms (halfwidth katakana 0xFF61+ stay narrow)
        (cp >= 0xFFE0 && cp <= 0xFFE6) ||   // Fullwidth signs
        (cp >= 0x20000 && cp <= 0x3FFFD);   // Supplementary ideographic plane (CJK Ext B+)

    private void ClampHorizontalScroll()
    {
        var maxX = Math.Max(0f, ContentWidth() - Position.Width);
        if (_scrollX < 0f) _scrollX = 0f;
        else if (_scrollX > maxX) _scrollX = maxX;
    }

    private void EnsureMetrics(ICanvas c)
    {
        if (!_buttonMetricsResolved)
        {
            var s = _loc.Strings.Value;
            _stageBtnTextWidth = c.MeasureTextWidth(s.DiffHunkStage, HunkButtonTextStyle);
            _unstageBtnTextWidth = c.MeasureTextWidth(s.DiffHunkUnstage, HunkButtonTextStyle);
            _discardBtnTextWidth = c.MeasureTextWidth(s.DiffHunkDiscard, HunkButtonTextStyle);
            _buttonMetricsResolved = _stageBtnTextWidth > 0;
        }

        if (_metricsResolved) return;
        _lineHeight = c.MeasureTextLineHeight(MonoMetricsStyle);
        // One real measurement of a representative glyph is more honest than the 0.6 ratio
        // heuristic; falls back to the heuristic if the canvas reports nothing usable.
        var measured = c.MeasureTextWidth("0", MonoMetricsStyle);
        _monoAdvance = measured > 0 ? measured : AssumedFontSize * FallbackMonoAdvanceRatio;
        // Recompute gutter width from the real advance so it lines up with actual digits.
        var digitsTotal = Math.Max(1, GutterDigitCount());
        _gutterWidth = digitsTotal * _monoAdvance + 8f;
        _metricsResolved = true;

        // Resolved row height feeds the widget; it'll re-clamp its scroll on next draw.
        if (Math.Abs(_list.RowHeight - _lineHeight) > 0.0001f)
        {
            _list.RowHeight = _lineHeight;
            _list.NotifyItemsChanged();
        }
    }

    private float GetButtonTextWidth(HunkAction action) => action switch
    {
        HunkAction.Stage => _stageBtnTextWidth,
        HunkAction.Unstage => _unstageBtnTextWidth,
        HunkAction.Discard => _discardBtnTextWidth,
        _ => 0f,
    };

    private int GutterDigitCount()
    {
        int maxDigits = 1;
        foreach (var row in _rows)
        {
            if (row is DiffRow.Line l)
            {
                if (l.OldNumber.Length > maxDigits) maxDigits = l.OldNumber.Length;
                if (l.NewNumber.Length > maxDigits) maxDigits = l.NewNumber.Length;
            }
        }
        return maxDigits;
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        var pos = Position;
        var z = GetDrawZIndex();

        c.DrawRect(new DrawRectInputs
        {
            Position = pos,
            Style = SolidBgStyle(_styles.Background),
            ZIndex = z,
        });

        switch (_renderState)
        {
            case DiffRenderState.Placeholder p:
                DrawPlaceholder(c, pos, p.Text, _styles.PlaceholderText, z + 1);
                NotifyScrollChanged(viewportFits: true);
                return;
            case DiffRenderState.Conflict:
                // The embedded pane swaps in the rich resolution view; this fallback is only
                // hit by the pop-out window, which has no resolution UI.
                DrawPlaceholder(c, pos, _loc.Strings.Value.DiffResolveInMain, _styles.PlaceholderText, z + 1);
                NotifyScrollChanged(viewportFits: true);
                return;
            case DiffRenderState.Loaded loaded when loaded.Result.ErrorMessage != null:
                DrawPlaceholder(c, pos, loaded.Result.ErrorMessage, _styles.ErrorText, z + 1);
                NotifyScrollChanged(viewportFits: true);
                return;
            case DiffRenderState.Loaded loaded when loaded.Result.IsBinary:
                DrawPlaceholder(c, pos, _loc.Strings.Value.DiffBinaryNotShown, _styles.PlaceholderText, z + 1);
                NotifyScrollChanged(viewportFits: true);
                return;
            case DiffRenderState.Loaded when _rows.Count == 0:
                DrawPlaceholder(c, pos, _loc.Strings.Value.DiffNoChanges, _styles.PlaceholderText, z + 1);
                NotifyScrollChanged(viewportFits: true);
                return;
        }

        EnsureMetrics(c);
        ClampHorizontalScroll();
        ReassertPendingScroll();
        NotifyScrollChanged(viewportFits: false);
    }

    // Re-applies a pending programmatic scroll until it takes (the scrollbar's hidden→visible
    // transition can echo a stale 0 back over it) or a short frame budget expires. Clearing on
    // arrival hands scrolling back to the user.
    private void ReassertPendingScroll()
    {
        if (_pendingScrollY is not float want) return;
        var clamped = ClampScrollTarget(want);
        if (Math.Abs(_list.ScrollY - clamped) <= 0.5f || --_pendingScrollFrames < 0)
        {
            _pendingScrollY = null;
            return;
        }
        _list.SetScrollY(clamped);
    }

    private float ClampScrollTarget(float y)
    {
        var max = Math.Max(0f, _rows.Count * _lineHeight - _list.Position.Height);
        return Math.Clamp(y, 0f, max);
    }

    // Sets a vertical scroll offset that should survive the next few frames' scrollbar churn.
    private void SetScrollTarget(float y)
    {
        _pendingScrollY = y;
        _pendingScrollFrames = 8;
        _list.SetScrollY(y);
    }

    private void DrawDiffRowAt(ICanvas c, RectF rowRect, int rowIndex, RowRenderState state, int z)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;

        // Apply horizontal scroll inside the widget's row rect. Vertical position comes
        // from the widget; horizontal position is our concern.
        var rowLeft = rowRect.Left - _scrollX;
        var rowBottom = rowRect.Bottom;
        var rowWidth = ContentWidth();

        var hunkIndex = rowIndex < _rowToHunk.Length ? _rowToHunk[rowIndex] : -1;
        var isHoveredHunk = hunkIndex >= 0 && hunkIndex == _hoveredHunkIndex;
        var showButtons = isHoveredHunk && rowIndex == ButtonRowFor(hunkIndex) && HasHunkButtons();

        switch (_rows[rowIndex])
        {
            case DiffRow.Banner b:
                DrawBannerRow(c, b, rowLeft, rowBottom, rowWidth, z);
                break;
            case DiffRow.HunkSeparator s:
                DrawHunkSeparatorRow(c, s, rowIndex, rowLeft, rowBottom, rowWidth, z);
                break;
            case DiffRow.Tear t:
                DrawTearRow(c, t, rowIndex, rowLeft, rowBottom, rowWidth, z);
                break;
            case DiffRow.Line l:
                DrawLineRow(c, l, rowLeft, rowBottom, rowWidth, z);
                break;
        }

        if (isHoveredHunk)
            DrawHunkOutlineForRow(c, rowRect, rowIndex, hunkIndex, z + 5);
        if (showButtons)
            DrawHunkButtons(c, rowRect, hunkIndex, z + 6);
    }

    private int ButtonRowFor(int hunkIndex)
    {
        if (hunkIndex < 0 || hunkIndex >= _hunkRanges.Count) return -1;
        var range = _hunkRanges[hunkIndex];
        return Math.Min(range.FirstRow + 1, range.LastRow);
    }

    private bool HasHunkButtons()
    {
        if (!_hunksPatchable) return false;
        return _diffSide is DiffSide.Unstaged or DiffSide.Staged;
    }

    private float GetButtonsTotalWidth()
    {
        var actions = GetButtonActions();
        if (actions.Length == 0) return 0f;
        var total = 0f;
        for (var i = 0; i < actions.Length; i++)
        {
            total += GetButtonTextWidth(actions[i]) + HunkButtonPaddingX * 2;
            if (i > 0) total += HunkButtonGap;
        }
        return total;
    }

    private HunkAction[] GetButtonActions() => _diffSide switch
    {
        DiffSide.Unstaged => new[] { HunkAction.Stage, HunkAction.Discard },
        DiffSide.Staged => new[] { HunkAction.Unstage },
        _ => Array.Empty<HunkAction>(),
    };

    private string ButtonLabel(HunkAction action)
    {
        var s = _loc.Strings.Value;
        return action switch
        {
            HunkAction.Stage => s.DiffHunkStage,
            HunkAction.Unstage => s.DiffHunkUnstage,
            HunkAction.Discard => s.DiffHunkDiscard,
            _ => string.Empty,
        };
    }

    private void DrawHunkButtons(ICanvas c, RectF rowRect, int hunkIndex, int z)
    {
        var actions = GetButtonActions();
        if (actions.Length == 0) return;

        var totalWidth = GetButtonsTotalWidth();
        var x = rowRect.Right - HunkButtonsMarginRight - totalWidth;
        var btnBottom = rowRect.Top - HunkButtonsTopInset - HunkButtonHeight;

        for (var i = 0; i < actions.Length; i++)
        {
            var label = ButtonLabel(actions[i]);
            var width = GetButtonTextWidth(actions[i]) + HunkButtonPaddingX * 2;
            var rect = new RectF(x, btnBottom, width, HunkButtonHeight);
            var hovered = hunkIndex == _hoveredHunkIndex && actions[i] == _hoveredButton;
            var bg = hovered ? _buttonStyles.BackgroundHover : _buttonStyles.BackgroundIdle;

            c.DrawRect(new DrawRectInputs
            {
                Position = rect,
                Style = new RectStyle
                {
                    BackgroundColor = bg,
                    BorderColor = BorderColorStyle.All(_buttonStyles.Border),
                    BorderSize = BorderSizeStyle.All(1),
                    BorderRadius = BorderRadiusStyle.All(Radius.Sm),
                },
                ZIndex = z,
            });

            HunkButtonTextStyle.TextColor = _buttonStyles.Text;
            c.DrawText(new DrawTextInputs
            {
                Position = rect,
                Text = label,
                Style = HunkButtonTextStyle,
                ZIndex = z + 1,
            });

            x += width + HunkButtonGap;
        }
    }

    private void DrawHunkOutlineForRow(ICanvas c, RectF rowRect, int rowIndex, int hunkIndex, int z)
    {
        if (hunkIndex < 0 || hunkIndex >= _hunkRanges.Count) return;
        var range = _hunkRanges[hunkIndex];

        // Left + right edges on every row of the hunk.
        var left = rowRect.Left;
        var right = rowRect.Right - HunkOutlineThickness;
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(left, rowRect.Bottom, HunkOutlineThickness, rowRect.Height),
            Style = SolidBgStyle(_styles.HunkOutline),
            ZIndex = z,
        });
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(right, rowRect.Bottom, HunkOutlineThickness, rowRect.Height),
            Style = SolidBgStyle(_styles.HunkOutline),
            ZIndex = z,
        });

        // Top edge on the header row, bottom edge on the last row.
        if (rowIndex == range.FirstRow)
        {
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(left, rowRect.Top - HunkOutlineThickness, rowRect.Width, HunkOutlineThickness),
                Style = SolidBgStyle(_styles.HunkOutline),
                ZIndex = z,
            });
        }
        if (rowIndex == range.LastRow)
        {
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(left, rowRect.Bottom, rowRect.Width, HunkOutlineThickness),
                Style = SolidBgStyle(_styles.HunkOutline),
                ZIndex = z,
            });
        }
    }

    private void DrawBannerRow(ICanvas c, DiffRow.Banner b, float left, float bottom, float width, int z)
    {
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(left, bottom, width, _lineHeight),
            Style = SolidBgStyle(_styles.SectionBackground),
            ZIndex = z,
        });
        DrawMonoText(c, b.Text, left + BannerPaddingX, bottom,
            width - BannerPaddingX * 2, _styles.SectionMutedText, TextAlignment.Start, z + 1);
    }

    private void DrawHunkSeparatorRow(
        ICanvas c, DiffRow.HunkSeparator s, int rowIndex, float left, float bottom, float width, int z)
    {
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(left, bottom, width, _lineHeight),
            Style = SolidBgStyle(_styles.SectionBackground),
            ZIndex = z,
        });

        var textX = left + BannerPaddingX;
        if (s.Gap is { } gap)
        {
            // Expander icon at the far left, over the gutter columns; the bar text shifts
            // right of it.
            textX = Math.Max(textX, DrawExpanderIcons(c, gap, rowIndex, left, bottom, z) + BannerPaddingX);
        }

        var cursorX = textX;
        if (s.Range.Length > 0)
        {
            var rangeWidth = s.Range.Length * _monoAdvance;
            DrawMonoText(c, s.Range, cursorX, bottom, rangeWidth,
                _styles.HunkSeparatorRangeText, TextAlignment.Start, z + 1);
            cursorX += rangeWidth + HunkHeaderGap;
        }

        if (s.Header != null)
        {
            var headerWidth = Math.Max(0f, left + width - BannerPaddingX - cursorX);
            if (headerWidth > 0)
            {
                DrawMonoText(c, s.Header, cursorX, bottom, headerWidth,
                    _styles.SectionMutedText, TextAlignment.Start, z + 1);
                cursorX += Math.Min(VisualCells(s.Header) * _monoAdvance, headerWidth) + HunkHeaderGap;
            }
        }

        // The muted "… N hidden lines" label; omitted on the EOF bar until the count is exact.
        if (s.Gap is { HiddenCount: int hidden })
        {
            var label = _loc.Strings.Value.DiffHiddenLines(hidden);
            var labelWidth = Math.Max(0f, left + width - BannerPaddingX - cursorX);
            if (labelWidth > 0)
                DrawMonoText(c, label, cursorX, bottom, labelWidth,
                    _styles.SectionMutedText, TextAlignment.Start, z + 1);
        }
    }

    // Draws a GapBar's expander icons (accent glyphs, a filled chip under the hovered one) and
    // returns the x just past the last cell. Shared by the separator bars and the tear row.
    private float DrawExpanderIcons(ICanvas c, GapBar gap, int rowIndex, float left, float bottom, int z)
    {
        var x = left + ExpanderPadLeft;
        foreach (var dir in ExpanderIconsFor(gap))
        {
            var hovered = rowIndex == _hoveredExpanderRow && dir == _hoveredExpanderDir;
            if (hovered)
            {
                c.DrawRect(new DrawRectInputs
                {
                    Position = new RectF(
                        x, bottom + ExpanderChipInsetY,
                        ExpanderCellWidth - ExpanderChipGap, _lineHeight - ExpanderChipInsetY * 2),
                    Style = new RectStyle
                    {
                        BackgroundColor = _styles.ExpanderHoverBackground,
                        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
                    },
                    ZIndex = z + 1,
                });
            }
            ExpanderIconStyle.TextColor = hovered ? _styles.ExpanderHoverIcon : _styles.ExpanderIcon;
            c.DrawText(new DrawTextInputs
            {
                Position = new RectF(x, bottom, ExpanderCellWidth - ExpanderChipGap, _lineHeight),
                Text = ExpanderGlyph(dir),
                Style = ExpanderIconStyle,
                ZIndex = z + 2,
            });
            x += ExpanderCellWidth;
        }
        return x;
    }

    private static string ExpanderGlyph(GapExpandDirection dir) => dir switch
    {
        GapExpandDirection.Down => LucideIcons.ChevronDown,
        GapExpandDirection.Up => LucideIcons.ChevronUp,
        _ => LucideIcons.UnfoldVertical,
    };

    // The torn break between a split gap's two bars: plain background with a jagged zigzag
    // along each row edge — the ragged ends of the bar strips above and below, as if the file
    // strip between them were torn out — plus the unfold-all affordance and the hidden count.
    private void DrawTearRow(ICanvas c, DiffRow.Tear t, int rowIndex, float left, float bottom, float width, int z)
    {
        var cursorX = DrawExpanderIcons(c, t.Gap, rowIndex, left, bottom, z) + BannerPaddingX;
        if (t.Gap.HiddenCount is int hidden)
        {
            var label = _loc.Strings.Value.DiffHiddenLines(hidden);
            var labelWidth = VisualCells(label) * _monoAdvance;
            DrawMonoText(c, label, cursorX, bottom, labelWidth,
                _styles.SectionMutedText, TextAlignment.Start, z + 1);
            cursorX += labelWidth + HunkHeaderGap;
        }
        DrawTearLine(c, cursorX, left + width, bottom + _lineHeight / 2f, z + 1);
    }

    private const float TearHalfPeriod = 9f;
    private const float TearAmplitude = 4.5f;
    private const float TearThickness = 1.25f;

    // A thin zigzag polyline centered on the row. Segment endpoints derive from the tear's
    // content-space start, so the pattern holds its phase under horizontal scroll; only the
    // segments intersecting the viewport are issued (the pattern can span the whole content
    // width, most of it scrolled out of view).
    private void DrawTearLine(ICanvas c, float from, float to, float centerY, int z)
    {
        if (to - from < TearHalfPeriod * 2) return;
        var listPos = _list.Position;
        var visFrom = Math.Max(from, listPos.Left - TearHalfPeriod);
        var visTo = Math.Min(to, listPos.Right + TearHalfPeriod);
        if (visTo <= visFrom) return;

        var kFrom = Math.Max(0, (int)((visFrom - from) / TearHalfPeriod));
        var kTo = Math.Min((int)((to - from) / TearHalfPeriod), (int)((visTo - from) / TearHalfPeriod) + 1);
        var color = _styles.LineNumberText;
        for (var k = kFrom; k < kTo; k++)
        {
            var down = (k & 1) == 0;
            c.DrawLine(new DrawLineInputs
            {
                Start = new PointF(from + k * TearHalfPeriod, centerY + (down ? TearAmplitude : -TearAmplitude)),
                End = new PointF(from + (k + 1) * TearHalfPeriod, centerY + (down ? -TearAmplitude : TearAmplitude)),
                Thickness = TearThickness,
                Color = color,
                ZIndex = z,
            });
        }
    }

    private static readonly GapExpandDirection[] NoExpanders = Array.Empty<GapExpandDirection>();
    private static readonly GapExpandDirection[] UnfoldOnly = { GapExpandDirection.All };
    private static readonly GapExpandDirection[] DownOnly = { GapExpandDirection.Down };
    private static readonly GapExpandDirection[] UpOnly = { GapExpandDirection.Up };
    private static readonly GapExpandDirection[] DownAndUp = { GapExpandDirection.Down, GapExpandDirection.Up };

    private static GapExpandDirection[] ExpanderIconsFor(GapBar gap)
    {
        if (gap.ShowUnfold) return UnfoldOnly;
        if (gap.ShowDown && gap.ShowUp) return DownAndUp;
        if (gap.ShowDown) return DownOnly;
        if (gap.ShowUp) return UpOnly;
        return NoExpanders;
    }


    private sealed record HunkRowRange(int HunkIndex, int FirstRow, int LastRow);

    private void DrawLineRow(ICanvas c, DiffRow.Line l, float left, float bottom, float width, int z)
    {
        var (glyph, glyphColor) = l.Kind switch
        {
            DiffLineKind.Added => ("+", _styles.LineAddedGlyph),
            DiffLineKind.Removed => ("-", _styles.LineRemovedGlyph),
            _ => (" ", _styles.LineContextGlyph),
        };
        var bg = l.Kind switch
        {
            DiffLineKind.Added => _styles.LineAddedBackground,
            DiffLineKind.Removed => _styles.LineRemovedBackground,
            _ => _styles.Background,
        };

        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(left, bottom, width, _lineHeight),
            Style = SolidBgStyle(bg),
            ZIndex = z,
        });

        var x = left;
        // Full-file mode shows only the new-side gutter; diff mode shows old|new.
        if (!_singleGutter)
        {
            DrawMonoText(c, l.OldNumber, x, bottom, _gutterWidth,
                _styles.LineNumberText, TextAlignment.End, z + 2);
            x += _gutterWidth + 4f;
        }
        DrawMonoText(c, l.NewNumber, x, bottom, _gutterWidth,
            _styles.LineNumberText, TextAlignment.End, z + 2);
        x += _gutterWidth + 4f;
        DrawMonoText(c, glyph, x, bottom, GlyphColumnWidth, glyphColor, TextAlignment.Center, z + 2);
        x += GlyphColumnWidth + 4f;

        // Intra-line emphasis: a stronger background tint over the changed characters, layered
        // between the line bg (z) and the text (z + 2). Walk the ranges incrementally, carrying
        // cx forward exactly as DrawLineText does, never re-measuring from column 0.
        if (l.Emphasis is { Count: > 0 } ranges)
        {
            var emBg = l.Kind == DiffLineKind.Removed
                ? _styles.LineRemovedEmphasisBackground
                : _styles.LineAddedEmphasisBackground;
            var len = l.Text.Length;
            var col = 0;
            var cx = x;
            foreach (var rng in ranges)
            {
                // ForPair promises sorted, non-overlapping, in-bounds ranges; this clamp is the
                // backstop so a regression there shows up as a wrong-looking rect, never a
                // per-frame Substring crash in the render loop.
                var start = Math.Clamp(rng.Start, col, len);
                var end = Math.Clamp(rng.Start + rng.Length, start, len);
                if (start > col)
                    cx += c.MeasureTextWidth(l.Text.Substring(col, start - col), MonoStartStyle);
                var w = c.MeasureTextWidth(l.Text.Substring(start, end - start), MonoStartStyle);
                c.DrawRect(new DrawRectInputs
                {
                    Position = new RectF(cx, bottom, w, _lineHeight),
                    Style = SolidBgStyle(emBg),
                    ZIndex = z + 1,
                });
                cx += w;
                col = end;
            }
        }

        DrawLineText(c, l, x, bottom, left + width, z + 2);
    }

    // Draws the line's text either as one run (no spans → plain, identical to before) or as a
    // sequence of colored runs interleaved with base-colored gaps. The font is monospace, so
    // each run sits at textStart + column*advance and every DrawText batches into one GPU draw.
    private void DrawLineText(ICanvas c, DiffRow.Line l, float textStart, float bottom, float maxRight, int z)
    {
        var spans = l.Spans;
        if (spans == null || spans.Count == 0)
        {
            DrawMonoText(c, l.Text, textStart, bottom, Math.Max(0f, maxRight - textStart),
                _styles.LineText, TextAlignment.Start, z);
            return;
        }

        var text = l.Text;
        var len = text.Length;
        var col = 0;
        var x = textStart;
        foreach (var span in spans)
        {
            var start = Math.Clamp(span.Start, 0, len);
            var end = Math.Clamp(span.Start + span.Length, 0, len);
            if (start > col)
                x = DrawTextRun(c, text, col, start, x, bottom, maxRight, _styles.LineText, z);
            if (end > start)
                x = DrawTextRun(c, text, start, end, x, bottom, maxRight, SlotColor(span.Slot), z);
            if (end > col) col = end;
        }
        if (col < len)
            DrawTextRun(c, text, col, len, x, bottom, maxRight, _styles.LineText, z);
    }

    // Draws text[from..to) at x and returns x advanced by the run's measured width, so colored
    // runs abut exactly where the shaper lays glyphs — wide CJK glyphs included, not just the
    // fixed monospace cell. Latin is pixel-identical, since its advance is the cell width.
    private float DrawTextRun(
        ICanvas c, string text, int from, int to, float x, float bottom, float maxRight, uint color, int z)
    {
        if (to <= from) return x;
        var run = text.Substring(from, to - from);
        var w = c.MeasureTextWidth(run, MonoStartStyle);
        if (x < maxRight)
            DrawMonoText(c, run, x, bottom, Math.Max(0f, maxRight - x), color, TextAlignment.Start, z);
        return x + w;
    }

    private uint SlotColor(TokenColorSlot slot) => slot switch
    {
        TokenColorSlot.Keyword => _styles.Syntax.Keyword,
        TokenColorSlot.String => _styles.Syntax.String,
        TokenColorSlot.Comment => _styles.Syntax.Comment,
        TokenColorSlot.Number => _styles.Syntax.Number,
        TokenColorSlot.Type => _styles.Syntax.Type,
        TokenColorSlot.Function => _styles.Syntax.Function,
        TokenColorSlot.Variable => _styles.Syntax.Variable,
        TokenColorSlot.Operator => _styles.Syntax.Operator,
        TokenColorSlot.Punctuation => _styles.Syntax.Punctuation,
        TokenColorSlot.Constant => _styles.Syntax.Constant,
        TokenColorSlot.Heading => _styles.Syntax.Heading,
        TokenColorSlot.Emphasis => _styles.Syntax.Emphasis,
        TokenColorSlot.Link => _styles.Syntax.Link,
        TokenColorSlot.Code => _styles.Syntax.Code,
        TokenColorSlot.Quote => _styles.Syntax.Quote,
        _ => _styles.LineText,
    };

    private void DrawMonoText(
        ICanvas c, string text, float left, float bottom, float width,
        uint color, TextAlignment alignment, int z)
    {
        if (width <= 0 || string.IsNullOrEmpty(text)) return;
        var style = alignment switch
        {
            TextAlignment.End => MonoEndStyle,
            TextAlignment.Center => MonoCenterStyle,
            _ => MonoStartStyle,
        };
        style.TextColor = color;
        c.DrawText(new DrawTextInputs
        {
            Position = new RectF(left, bottom, width, _lineHeight),
            Text = text,
            Style = style,
            ZIndex = z,
        });
    }

    private void DrawPlaceholder(ICanvas c, RectF pos, string text, uint color, int z)
    {
        PlaceholderStyle.TextColor = color;
        c.DrawText(new DrawTextInputs
        {
            Position = pos,
            Text = text,
            Style = PlaceholderStyle,
            ZIndex = z,
        });
    }

    public void OnHunkPointerMove(PointF point)
    {
        // Expander hover is independent of hunk buttons: it applies to read-only sides too.
        var expander = HitTestExpander(point);
        SetExpanderHover(expander?.Row ?? -1, expander?.Dir ?? default);

        if (!HasHunkButtons()) { SetHunkHover(-1, HunkAction.None); return; }

        var listPos = _list.Position;
        if (!listPos.ContainsPoint(point)) { SetHunkHover(-1, HunkAction.None); return; }

        var rowIndex = HitTestListRow(point);
        var hunkIndex = (rowIndex >= 0 && rowIndex < _rowToHunk.Length) ? _rowToHunk[rowIndex] : -1;
        var button = HunkAction.None;
        if (hunkIndex >= 0)
            button = HitTestButton(point, ButtonRowFor(hunkIndex));
        SetHunkHover(hunkIndex, button);
    }

    public void OnHunkPointerExit()
    {
        SetExpanderHover(-1, default);
        SetHunkHover(-1, HunkAction.None);
    }

    public bool TryClickExpander(PointF point)
    {
        if (HitTestExpander(point) is not { } hit) return false;
        OnExpandGap?.Invoke(hit.GapIndex, hit.Dir);
        return true;
    }

    // The expander icon under the pointer, if any: row hit first (full row height), then the
    // per-icon cell check mirroring DrawExpanderIcons' geometry.
    private (int Row, int GapIndex, GapExpandDirection Dir)? HitTestExpander(PointF point)
    {
        if (_lineHeight <= 0) return null;
        var listPos = _list.Position;
        if (!listPos.ContainsPoint(point)) return null;
        var rowIndex = HitTestListRow(point);
        if (rowIndex < 0 || GapBarOf(_rows[rowIndex]) is not { } gap) return null;

        var x = listPos.Left - _scrollX + ExpanderPadLeft;
        foreach (var dir in ExpanderIconsFor(gap))
        {
            if (point.X >= x && point.X <= x + ExpanderCellWidth) return (rowIndex, gap.GapIndex, dir);
            x += ExpanderCellWidth;
        }
        return null;
    }

    private static GapBar? GapBarOf(DiffRow row) => row switch
    {
        DiffRow.HunkSeparator { Gap: { } g } => g,
        DiffRow.Tear t => t.Gap,
        _ => null,
    };

    private void SetExpanderHover(int rowIndex, GapExpandDirection dir)
    {
        if (_hoveredExpanderRow == rowIndex && _hoveredExpanderDir == dir) return;
        _hoveredExpanderRow = rowIndex;
        _hoveredExpanderDir = dir;
        SetDirty();
    }

    public bool TryClickHunkAction(PointF point)
    {
        if (!HasHunkButtons()) return false;
        var listPos = _list.Position;
        if (!listPos.ContainsPoint(point)) return false;

        var rowIndex = HitTestListRow(point);
        if (rowIndex < 0 || rowIndex >= _rowToHunk.Length) return false;
        var hunkIndex = _rowToHunk[rowIndex];
        if (hunkIndex < 0) return false;

        var button = HitTestButton(point, ButtonRowFor(hunkIndex));
        if (button == HunkAction.None) return false;

        switch (button)
        {
            case HunkAction.Stage: OnStageHunk?.Invoke(hunkIndex); break;
            case HunkAction.Unstage: OnUnstageHunk?.Invoke(hunkIndex); break;
            case HunkAction.Discard: OnDiscardHunk?.Invoke(hunkIndex); break;
        }
        return true;
    }

    private void SetHunkHover(int hunkIndex, HunkAction button)
    {
        if (_hoveredHunkIndex == hunkIndex && _hoveredButton == button) return;
        _hoveredHunkIndex = hunkIndex;
        _hoveredButton = button;
        SetDirty();
    }

    private int HitTestListRow(PointF point)
    {
        if (_lineHeight <= 0) return -1;
        var listPos = _list.Position;
        var distFromTop = listPos.Top - point.Y;
        var idx = (int)((distFromTop + _list.ScrollY) / _lineHeight);
        if (idx < 0 || idx >= _rows.Count) return -1;
        return idx;
    }

    private HunkAction HitTestButton(PointF point, int buttonRowIndex)
    {
        var actions = GetButtonActions();
        if (actions.Length == 0 || buttonRowIndex < 0) return HunkAction.None;

        var listPos = _list.Position;
        var rowTop = listPos.Top + _list.ScrollY - buttonRowIndex * _lineHeight;
        var btnTop = rowTop - HunkButtonsTopInset;
        var btnBottom = btnTop - HunkButtonHeight;
        if (point.Y < btnBottom || point.Y > btnTop) return HunkAction.None;

        var totalWidth = GetButtonsTotalWidth();
        var x = listPos.Right - HunkButtonsMarginRight - totalWidth;
        for (var i = 0; i < actions.Length; i++)
        {
            var width = GetButtonTextWidth(actions[i]) + HunkButtonPaddingX * 2;
            if (point.X >= x && point.X <= x + width) return actions[i];
            x += width + HunkButtonGap;
        }
        return HunkAction.None;
    }

    private void NotifyScrollChanged(bool viewportFits)
    {
        float normalizedY, normalizedX, vScale, hScale;
        if (viewportFits)
        {
            normalizedY = 0f;
            normalizedX = 0f;
            vScale = 1f;
            hScale = 1f;
        }
        else
        {
            var contentH = ContentHeight();
            var contentW = ContentWidth();
            var vph = Position.Height;
            var vpw = Position.Width;

            if (contentH <= vph || vph <= 0)
            {
                vScale = 1f;
                normalizedY = 0f;
            }
            else
            {
                vScale = vph / contentH;
                var range = contentH - vph;
                normalizedY = Math.Clamp(_list.ScrollY / range, 0f, 1f);
            }

            if (contentW <= vpw || vpw <= 0)
            {
                hScale = 1f;
                normalizedX = 0f;
            }
            else
            {
                hScale = vpw / contentW;
                var range = contentW - vpw;
                normalizedX = Math.Clamp(_scrollX / range, 0f, 1f);
            }
        }

        VerticalScale = vScale;
        HorizontalScale = hScale;

        // Dedup against the last published value — otherwise we'd retrigger scrollbar
        // layout every frame, even when nothing actually changed.
        if (Math.Abs(vScale - _lastVerticalScale) > 0.0001f ||
            Math.Abs(normalizedY - _lastNormalizedY) > 0.0001f)
        {
            _lastVerticalScale = vScale;
            _lastNormalizedY = normalizedY;
            VerticalScrollPositionChanged?.Invoke(normalizedY);
        }
        if (Math.Abs(hScale - _lastHorizontalScale) > 0.0001f ||
            Math.Abs(normalizedX - _lastNormalizedX) > 0.0001f)
        {
            _lastHorizontalScale = hScale;
            _lastNormalizedX = normalizedX;
            HorizontalScrollPositionChanged?.Invoke(normalizedX);
        }
    }

    private static RectStyle SolidBgStyle(uint color) => new() { BackgroundColor = color };

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
