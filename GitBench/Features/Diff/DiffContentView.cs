using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.VirtualRowList;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Diff;

/// <summary>
/// Virtualized diff body. Vertical scroll, hit-test boilerplate, and visible-row culling
/// live in a child <see cref="VirtualRowListView"/>; row flattening and per-row drawing live
/// in the shared <see cref="DiffRowSet"/> / <see cref="DiffRowPainter"/>; this view keeps
/// horizontal scroll (the widget is vertical-only), font-metric resolution, and the hunk
/// hover chrome (outline + Stage/Unstage/Discard buttons). Emits normalized scroll-position
/// and scale updates on both axes so an external scrollbar sync controller can drive the
/// scrollbars.
/// </summary>
internal enum HunkAction { None, Stage, Unstage, Discard }

internal sealed class DiffContentView : View, IScrollableContent, IDiffSelectionSurface,
    Features.LanguageServers.IHoverSurface, Features.LanguageServers.IDefinitionSurface
{
    private const float AssumedFontSize = FontSize.Body;
    // Fallback mono advance ratio if the canvas isn't available yet to measure a glyph.
    private const float FallbackMonoAdvanceRatio = 0.6f;

    private const float HunkOutlineThickness = 1f;

    private static readonly TextStyle PlaceholderStyle = new()
    {
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };

    public event Action<float>? VerticalScrollPositionChanged;

    /// <summary>The new-file line at the top of the viewport, or null while there is none to
    /// report. Raised only when it changes, and only once metrics have resolved — row geometry is
    /// what makes the question answerable, so a caller cannot ask before the first draw.</summary>
    public event Action<FileLine?>? TopVisibleLineChanged;

    /// <summary>A declaration's fold chevron was clicked, by the id its <see cref="FoldMark"/>
    /// carries. The owner decides what that means and hands back a new <see cref="FoldState"/>.</summary>
    public event Action<string>? OnToggleFold;

    /// <summary>A declaration's usages row was clicked. The answer — a list, a jump, a panel — is
    /// the owner's; this says which declaration was asked about, and where under the lens whatever
    /// answers should hang.</summary>
    public event Action<UsageLensTarget, PointF>? UsageLensActivated;
    public event Action<float>? HorizontalScrollPositionChanged;

    public float VerticalScale { get; private set; } = 1f;
    public float HorizontalScale { get; private set; } = 1f;

    private DiffContentStyles _styles = ThemeStyles.Dark.DiffContent;
    private DiffHunkButtonStyles _buttonStyles = ThemeStyles.Dark.DiffHunkButton;

    private DiffRenderState _renderState = new DiffRenderState.Placeholder("Select a file to view diff.");
    private DiffRowSet _rowSet = DiffRowSet.Empty;
    private DiffDiagnosticOverlay _diagnostics = DiffDiagnosticOverlay.Empty;
    private DiffSearchOverlay _search = DiffSearchOverlay.Empty;
    // Whether the hits in hand were found in the file currently rendered. Resolved when either of
    // those changes rather than per row, and it is the whole of what gates both the wash and the
    // reveal: the two arrive from separate bindings, so on a file switch one of them is briefly the
    // other file's, and those line numbers would land on whatever now sits at them.
    private bool _searchApplies;
    private UsageLensOverlay _usageLens = UsageLensOverlay.Empty;
    private bool _usageLensRows;
    private int _hoveredLensRow = -1;
    private FileSpan? _definitionLink;
    private readonly DiffRowPainter _painter;
    private float _gutterWidth;
    private float _lineHeight;
    private float _monoAdvance;
    private bool _metricsResolved;

    private DiffSide _diffSide;
    private bool _hunksPatchable;
    private int _hoveredHunkIndex = -1;
    private HunkAction _hoveredButton = HunkAction.None;
    private int _hoveredExpanderRow = -1;
    private readonly HunkButtonBar _buttonBar;
    private IReadOnlyList<WorkingTreeHunkState>? _hunkStates;

    public Action<int>? OnStageHunk { get; set; }
    public Action<int>? OnUnstageHunk { get; set; }
    public Action<int>? OnDiscardHunk { get; set; }
    public Action<int, GapExpandDirection>? OnExpandGap { get; set; }

    /// <summary>The same click held with <see cref="InputModifiers.Alt"/>: reveal the rest of the
    /// declaration rather than another fixed step of context.</summary>
    public Action<int, GapExpandDirection>? OnExpandGapToDeclaration { get; set; }

    private readonly VirtualRowListView _list;
    private readonly ILocalizationService _loc;
    private readonly Context _ctx;
    private readonly IMessageBus? _bus;
    private readonly DiffSelectionModel _selection = new();
    private readonly DiffSelectionController _selectionController;

    /// <summary>Whether a selection here offers the assistant's quick actions. Only the main
    /// window's diff sets it: the assistant overlay is a child of that window, so an answer asked
    /// for from a pop-out would arrive somewhere the reader is not looking.</summary>
    public bool AssistantActions { get; set; }

    private float _scrollX;
    // A programmatic vertical scroll target that must be re-asserted across frames. Setting a
    // non-zero scroll right as content changes can be clobbered: when taller content makes the
    // vertical scrollbar transition hidden→visible, the bar's layout echoes a stale position
    // (0) back through the sync controller. We re-apply the target for a few frames until the
    // bar settles and the value sticks, then release control so the user can scroll freely.
    private float? _pendingScrollY;
    private int _pendingScrollFrames;
    private FileLine? _pendingScrollLine;
    private FileSearchMatch? _pendingSearchReveal;
    private FileLine? _lastTopLine;
    private bool _topLinePublished;
    private FoldState? _foldState;
    private int _hoveredFoldRow = -1;
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
        _ctx = ctx;
        _bus = ctx.Get<IMessageBus>();
        _loc = ctx.Localization();
        _painter = new DiffRowPainter(_loc);
        _buttonBar = new HunkButtonBar(_loc);

        _list = new VirtualRowListView
        {
            RowHeight = AssumedFontSize, // placeholder until canvas-derived metrics resolve
            RowHeightAt = RowHeightAt,
            ItemBuilder = DrawDiffRowAt,
            ScrollWheelStep = Scrolling.WheelStep,
            CursorAt = CursorAt,
        };
        _list.ScrollChanged += () => NotifyScrollChanged(viewportFits: false);
        _list.HorizontalWheelHandler = OnHorizontalWheel;

        AddChildToSelf(_list);
        _list.UseController(input, () => new VirtualRowListController(_list));
        // Ordered: the hunk controller claims expander and button presses first, in the same
        // capture pass, so a click on either never starts a text selection.
        this.UseController(input, () => new DiffMouseController(this), EventPhaseFilter.Capture);
        _selectionController = new DiffSelectionController(this, input, ctx.Get<IClipboard>());
        this.UseController(input, _selectionController, EventPhaseFilter.Both);

        this.BindThemed(theme, s =>
        {
            _styles = s.DiffContent;
            _buttonStyles = s.DiffHunkButton;
            _painter.Styles = s.DiffContent;
            SetDirty();
        });

        // Placeholder/conflict text is custom-painted, so repaint on a live language switch.
        // Hunk-button labels are measured and cached; drop the cache so they re-measure in the
        // new language on the next draw.
        this.Bind(_loc.Strings, _ => { _buttonBar.InvalidateMetrics(); SetDirty(); });
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

    // The VM's per-hunk index states for the WorkingTree view (see
    // DiffViewModel.WorkingTreeHunkStates); aligned with the current render's hunk list.
    public void SetWorkingTreeHunkStates(IReadOnlyList<WorkingTreeHunkState>? states)
    {
        _hunkStates = states;
        SetDirty();
    }

    public void SetRenderState(DiffRenderState state)
    {
        // Capture the outgoing view's identity and position before rebuilding rows, so we can
        // preserve the reading position across a mode toggle and hold it across the async
        // highlight re-emit that follows. _renderState still holds the previous state here.
        var (prevPath, prevWasFullFile) = DescribeState(_renderState);
        var prevTopLine = TopVisibleNewLine();
        var prevScrollY = _list.ScrollY;
        var prevScrollX = _scrollX;
        var prevRowCount = _rowSet.Rows.Count;

        _renderState = state;
        _hoveredHunkIndex = -1;
        _hoveredButton = HunkAction.None;
        _hoveredExpanderRow = -1;
        _hoveredFoldRow = -1;
        _hoveredLensRow = -1;
        _hunksPatchable = false;
        _diffSide = DiffSide.Unstaged;
        // Metrics depend only on font, not content, but content width depends on metrics;
        // a fresh model forces a recompute on next draw.
        _metricsResolved = false;

        _rowSet = DiffRowSet.Build(state, _loc, FoldsFor(state), _usageLensRows);
        if (state is DiffRenderState.Loaded loaded)
        {
            _diffSide = loaded.Result.Side;
            _hunksPatchable = HunkPatchBuilder.CanPatchHunk(loaded.Result);
        }
        else if (state is DiffRenderState.FullFile fullFile)
        {
            _diffSide = fullFile.Side;
        }
        _gutterWidth = _rowSet.GutterDigits * AssumedFontSize * FallbackMonoAdvanceRatio + 8f;

        // Selection positions are row indices into the old row stream. A different file or a
        // different row count (a gap expanded, the mode toggled) invalidates them. A same-shape
        // re-emit — the async syntax highlight attaching — leaves them meaning what they meant.
        var (newPath, _) = DescribeState(state);
        if (newPath != prevPath || _rowSet.Rows.Count != prevRowCount)
            _selection.Clear();
        if (newPath != prevPath) _pendingScrollLine = null;
        // A different file republishes its top line even when the number is unchanged: it is a
        // different declaration at line 1.
        if (newPath != prevPath) _topLinePublished = false;

        RefreshSearchScope();
        _list.ItemCount = _rowSet.Rows.Count;
        _list.NotifyItemsChanged();
        ApplyScrollForTransition(state, prevPath, prevWasFullFile, prevTopLine, prevScrollY, prevScrollX);
        SetDirty();
    }

    /// <summary>
    /// Replaces the fold set and re-flattens, deliberately not through <see cref="SetRenderState"/>.
    /// That path resets horizontal scroll, and restores a *pixel* offset — which after a collapse
    /// above the viewport would silently move the reader onto a different line. This one re-anchors
    /// on the line they were reading instead.
    /// </summary>
    /// <remarks>
    /// The selection is cleared rather than remapped. <c>DiffTextPos</c> is a row index into the
    /// current stream, so an anchor at row 40 means a different line the moment rows disappear
    /// above it, and remapping anchors through the fold model is more work than folding.
    /// </remarks>
    public void SetFoldState(FoldState folds)
    {
        _foldState = folds;
        if (_renderState is not DiffRenderState.FullFile) { SetDirty(); return; }

        var topLine = TopVisibleNewLine();
        _rowSet = DiffRowSet.Build(_renderState, _loc, FoldsFor(_renderState), _usageLensRows);
        _selection.Clear();
        _hoveredFoldRow = -1;
        _hoveredLensRow = -1;
        _list.ItemCount = _rowSet.Rows.Count;
        _list.NotifyItemsChanged();
        if (topLine is { } line) ScrollToNewLine(line, leadIn: 0);
        SetDirty();
    }

    // A fold set belongs to one file. Holding it past a change of path would fold line ranges the
    // new file never agreed to, so it simply does not apply.
    private FoldState? FoldsFor(DiffRenderState state) =>
        _foldState is { } folds && DescribeState(state) is (string path, true) && folds.Path == path
            ? folds
            : null;

    // Lead-in rows kept above a "scroll to line" target so the line isn't flush against the top.
    private const int ScrollLeadIn = 3;

    // Chooses the scroll position for a freshly-built render: preserve the read line across a
    // toggle, hold the offset across same-state re-emits (highlight), or land on the first change
    // for a fresh full-file load. Falls back to the top — the prior behavior for plain diffs.
    private void ApplyScrollForTransition(
        DiffRenderState state, string? prevPath, bool prevWasFullFile,
        FileLine? prevTopLine, float prevScrollY, float prevScrollX)
    {
        var (newPath, newIsFullFile) = DescribeState(state);
        var sameFile = newPath != null && newPath == prevPath;
        // Horizontal travel is a property of the file being read, not of the render that carried
        // it, so it survives exactly what the vertical offset survives: a different file resets it,
        // a re-emit of the same one does not. An offset left overhanging narrower content is pulled
        // back by the clamp every draw already runs.
        _scrollX = sameFile ? prevScrollX : 0;

        if (sameFile)
        {
            // Same file. A flipped mode is a toggle → remap the top line into the new layout;
            // an unchanged mode is a re-emit (highlight attach, working-tree reload) → keep the
            // exact offset so neither the highlight nor a toggle's follow-up snaps to the top.
            if (newIsFullFile != prevWasFullFile && prevTopLine is { } top)
                ScrollToNewLine(top, ScrollLeadIn);
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
            ScrollToNewLine(new FileLine(first), ScrollLeadIn);
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

    // The new-file line of the topmost visible row, used to preserve the reading position across a
    // Diff↔FullFile toggle. Skips banners/separators and removed rows (no new-side number). Null
    // before metrics resolve or when no row from there down stands for a new-side line.
    public FileLine? TopVisibleNewLine()
    {
        var count = _rowSet.Rows.Count;
        if (_lineHeight <= 0 || count == 0) return null;
        var topIndex = Math.Clamp(_list.VisibleRange().First, 0, count - 1);
        for (var i = topIndex; i < count; i++)
            if (_rowSet.NewLineAt(new RowIndex(i)) is { } line) return line;
        return null;
    }

    // Scrolls to a new-file line, holding the target until it can be honoured. Row geometry needs
    // metrics, and metrics resolve on the first draw, so a jump asked for while the view is fresh —
    // the file browser's, on the frame it mounts — would otherwise be dropped.
    /// <summary>The find bar's hits, washed over the text. Held rather than folded into the rows:
    /// a query changes on every keystroke while the rows under it stand.</summary>
    public void SetSearch(DiffSearchOverlay search)
    {
        _search = search;
        _pendingSearchReveal = null;
        RefreshSearchScope();
        SetDirty();
    }

    private void RefreshSearchScope() =>
        _searchApplies = DescribeState(_renderState).Path is { } path && _search.IsFor(path);

    /// <summary>
    /// Brings a hit into view on both axes, and only as far as it has to: stepping through hits
    /// that are already on screen must leave the text where the reader is reading it.
    /// </summary>
    public void RevealSearchMatch(FileSearchMatch match)
    {
        _pendingSearchReveal = match;
        ApplyPendingSearchReveal();
        SetDirty();
    }

    // Held until it can be honoured: row geometry needs measured text, and the hits and the rows
    // arrive from two separate bindings in an order this view does not set — so a reveal waits for
    // the rows under it to be the ones its hit was found in.
    private void ApplyPendingSearchReveal()
    {
        if (_pendingSearchReveal is not { } match || _lineHeight <= 0 || !_searchApplies) return;
        _pendingSearchReveal = null;

        if (_rowSet.RowNearestNewLine(match.Line) is not { } row) return;
        if (_rowSet.Rows[row.Value] is not DiffRow.Line line) return;
        ScrollRowIntoView(row);
        ScrollColumnsIntoView(line.Text, match);
    }

    // Y grows upward here: a row is fully in view when its top is no higher than the viewport's top
    // and its bottom no lower than the viewport's bottom.
    private void ScrollRowIntoView(RowIndex row)
    {
        if (!_list.TryGetRowRect(row.Value, out var rect)) return;
        var view = _list.Position;
        if (rect.Top <= view.Top && rect.Bottom >= view.Bottom) return;

        var delta = rect.Top > view.Top ? view.Top - rect.Top : view.Bottom - rect.Bottom;
        SetScrollTarget(_list.ScrollY + delta);
    }

    // A hit far out on a long line is otherwise jumped to and still not on screen.
    private void ScrollColumnsIntoView(DiffLineText text, FileSearchMatch match)
    {
        if (_monoAdvance <= 0) return;

        var origin = DiffRowPainter.LineTextOriginX(
            _list.Position.Left, _gutterWidth, _rowSet.SingleGutter, _rowSet.FoldColumn,
            _rowSet.GlyphColumn);
        var left = origin + DiffText.CellsBefore(text.Expanded, text.ToExpanded(match.Start).Value) * _monoAdvance;
        var right = origin + DiffText.CellsBefore(text.Expanded, text.ToExpanded(match.End).Value) * _monoAdvance;

        // A few cells of run-up, so a hit brought into view reads as sitting inside a line rather
        // than as being clipped by the edge it was dragged to.
        var margin = RevealMarginCells * _monoAdvance;
        var viewLeft = _list.Position.Left + _scrollX;
        var viewRight = viewLeft + _list.Position.Width;

        var prev = _scrollX;
        if (left - margin < viewLeft) _scrollX -= viewLeft - left + margin;
        else if (right + margin > viewRight) _scrollX += right - viewRight + margin;
        ClampHorizontalScroll();
        if (_scrollX != prev) NotifyScrollChanged(viewportFits: false);
    }

    private const int RevealMarginCells = 4;

    public void RequestScrollToNewLine(FileLine line)
    {
        _pendingScrollLine = line;
        ApplyPendingScrollLine();
        SetDirty();
    }

    private void ApplyPendingScrollLine()
    {
        if (_pendingScrollLine is not { } line || _lineHeight <= 0) return;
        _pendingScrollLine = null;
        ScrollToNewLine(line, ScrollLeadIn);
    }

    // Scrolls so the row for the given new-file line sits leadIn rows below the top. No-op when no
    // row stands for it or for anything above it.
    public void ScrollToNewLine(FileLine line, int leadIn)
    {
        if (_lineHeight <= 0) return;
        if (_rowSet.RowNearestNewLine(line) is not { } row) return;
        SetScrollTarget(ContentOffsetOf(Math.Max(0, row.Value - leadIn)));
    }

    // A row's distance from the top of the content. Rows are not all one height, so this comes off
    // the widget's own offset table rather than a product: it places a row's top at
    // Position.Top + ScrollY − offset, and the offset is what that leaves behind.
    private float ContentOffsetOf(int rowIndex) =>
        _list.TryGetRowRect(rowIndex, out var rect) ? _list.Position.Top + _list.ScrollY - rect.Top : 0f;

    private float RowHeightAt(int rowIndex)
    {
        var height = _lineHeight > 0 ? _lineHeight : AssumedFontSize;
        var rows = _rowSet.Rows;
        return rowIndex >= 0 && rowIndex < rows.Count ? DiffRowMetrics.HeightOf(rows[rowIndex], height) : height;
    }

    private float ContentHeight()
    {
        if (_lineHeight <= 0) return 0f;
        return _list.ContentHeight;
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
        var gutters = _rowSet.SingleGutter ? _gutterWidth : _gutterWidth + _gutterWidth;
        var lineWidth = DiffRowPainter.MarkerLaneWidth
            + gutters + DiffRowPainter.FoldColumnWidthOf(_rowSet.FoldColumn)
            + DiffRowPainter.GlyphColumnWidthOf(_rowSet.GlyphColumn)
            + _rowSet.MaxRowCells * _monoAdvance + DiffRowPainter.BannerPaddingX;
        var bannerWidth = DiffRowPainter.BannerPaddingX * 2 + _rowSet.MaxRowCells * _monoAdvance;
        return Math.Max(lineWidth, bannerWidth);
    }

    private void ClampHorizontalScroll()
    {
        var maxX = Math.Max(0f, ContentWidth() - Position.Width);
        if (_scrollX < 0f) _scrollX = 0f;
        else if (_scrollX > maxX) _scrollX = maxX;
    }

    private void EnsureMetrics(ICanvas c)
    {
        _buttonBar.EnsureMetrics(c);

        if (_metricsResolved) return;
        _lineHeight = c.MeasureTextLineHeight(DiffRowPainter.MonoMetricsStyle);
        // One real measurement of a representative glyph is more honest than the 0.6 ratio
        // heuristic; falls back to the heuristic if the canvas reports nothing usable.
        var measured = c.MeasureTextWidth("0", DiffRowPainter.MonoMetricsStyle);
        _monoAdvance = measured > 0 ? measured : AssumedFontSize * FallbackMonoAdvanceRatio;
        // Recompute gutter width from the real advance so it lines up with actual digits.
        _gutterWidth = _rowSet.GutterDigits * _monoAdvance + 8f;
        _painter.LineHeight = _lineHeight;
        _painter.MonoAdvance = _monoAdvance;
        _metricsResolved = true;

        // Resolved row height feeds the widget's offset table; it'll re-clamp its scroll on next
        // draw. Heights are per-row, so the table has to be discarded, not just the base height.
        if (Math.Abs(_list.RowHeight - _lineHeight) > 0.0001f)
        {
            _list.RowHeight = _lineHeight;
            _list.InvalidateRowHeights();
        }
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        var pos = Position;
        var z = GetDrawZIndex();

        c.DrawRect(new DrawRectInputs
        {
            Position = pos,
            Style = new RectStyle { BackgroundColor = _styles.Background },
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
            case DiffRenderState.Loaded when _rowSet.Rows.Count == 0:
                DrawPlaceholder(c, pos, _loc.Strings.Value.DiffNoChanges, _styles.PlaceholderText, z + 1);
                NotifyScrollChanged(viewportFits: true);
                return;
        }

        EnsureMetrics(c);
        ClampHorizontalScroll();
        ApplyPendingScrollLine();
        ApplyPendingSearchReveal();
        ReassertPendingScroll();
        NotifyTopVisibleLine();
        _selectionController.Tick();
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

    private void NotifyTopVisibleLine()
    {
        var line = TopVisibleNewLine();
        if (_topLinePublished && line == _lastTopLine) return;
        _lastTopLine = line;
        _topLinePublished = true;
        TopVisibleLineChanged?.Invoke(line);
    }

    private float ClampScrollTarget(float y)
    {
        var max = Math.Max(0f, _list.ContentHeight - _list.Position.Height);
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
        var rows = _rowSet.Rows;
        if (rowIndex < 0 || rowIndex >= rows.Count) return;

        // Apply horizontal scroll inside the widget's row rect. Vertical position comes
        // from the widget; horizontal position is our concern.
        var rowLeft = rowRect.Left - _scrollX;
        var rowWidth = ContentWidth();

        var hunkIndex = _rowSet.HunkIndexOf(rowIndex);
        var isHoveredHunk = hunkIndex >= 0 && hunkIndex == _hoveredHunkIndex;
        var showButtons = isHoveredHunk && rowIndex == ButtonRowFor(hunkIndex) && HasHunkButtons();

        DiffRowSelection? selection = null;
        if (rows[rowIndex] is DiffRow.Line line
            && _selection.TryRowSpan(null, new RowIndex(rowIndex), line.Text.End, out var span))
            selection = span;

        _painter.DrawRow(c, rows[rowIndex], new DiffRowPaint(
            rowLeft, rowRect.Bottom, rowWidth, _gutterWidth, _rowSet.SingleGutter,
            ExpanderHovered: rowIndex == _hoveredExpanderRow,
            Viewport: _list.Position,
            Z: z,
            Selection: selection,
            FoldColumn: _rowSet.FoldColumn,
            FoldHovered: rowIndex == _hoveredFoldRow,
            Diagnostics: MarksOnRow(rowIndex),
            GlyphColumn: _rowSet.GlyphColumn,
            Link: LinkOnRow(rowIndex),
            Usages: UsagesOnRow(rowIndex),
            LensHovered: rowIndex == _hoveredLensRow,
            Search: SearchOnRow(rowIndex)));

        if (isHoveredHunk)
            DrawHunkOutlineForRow(c, rowRect, rowIndex, hunkIndex, z + 5);
        if (showButtons)
            _buttonBar.Draw(
                c, rowRect.Right, rowRect.Top,
                ActionsForHunk(hunkIndex),
                hunkIndex == _hoveredHunkIndex ? _hoveredButton : HunkAction.None,
                _buttonStyles,
                z + 6);
    }

    /// <summary>
    /// Replaces what the servers say about the file on screen. Deliberately not through
    /// <see cref="SetRenderState"/>: waves arrive for minutes while the same rows stay up, and
    /// re-flattening on each one would throw away scroll position and fold state to draw a
    /// squiggle.
    /// </summary>
    public void SetDiagnostics(DiffDiagnosticOverlay diagnostics)
    {
        if (ReferenceEquals(_diagnostics, diagnostics)) return;
        _diagnostics = diagnostics;
        SetDirty();
    }

    public IReadOnlyList<Lsp.Diagnostic> DiagnosticsOn(FileLine line) => _diagnostics.On(line);

    /// <summary>
    /// Replaces what is known about the file's usages. Deliberately not through
    /// <see cref="SetRenderState"/>, for the same reason <see cref="SetDiagnostics"/> is not:
    /// counts trickle in for as long as a file is open, and re-flattening on each would shuffle
    /// the file under the reader dozens of times while they were trying to read it.
    /// </summary>
    public void SetUsageLens(UsageLensOverlay usages)
    {
        if (ReferenceEquals(_usageLens, usages)) return;
        _usageLens = usages;
        SetDirty();
    }

    /// <summary>
    /// Whether declarations in a whole-file render carry a usages row. Off by default: nothing
    /// answers the question yet, and a row that stays blank forever is worse than no row. Flipping
    /// it re-flattens, so it is a setting rather than something that must be set first.
    /// </summary>
    public bool UsageLensRows
    {
        get => _usageLensRows;
        set
        {
            if (_usageLensRows == value) return;
            _usageLensRows = value;
            SetRenderState(_renderState);
        }
    }

    // What a lens row has to say, or null on every other row and on a declaration nothing has been
    // asked about yet.
    private UsageLensState? UsagesOnRow(int rowIndex) =>
        _rowSet.Rows[rowIndex] is DiffRow.Lens lens ? _usageLens.On(lens.At) : null;

    /// <summary>Every declaration carrying a usages row in the current render, in row order.
    /// Empty whenever the rows are off, and for a diff, which never grows them.</summary>
    public IReadOnlyList<UsageLensTarget> UsageLensTargets() => TargetsIn(0, _rowSet.Rows.Count - 1);

    /// <summary>
    /// The declarations whose usages rows are on screen. What decides which questions are worth
    /// asking: a file has more declarations than a reader can see, and a server answers about a
    /// symbol far more slowly than they can scroll past it.
    /// </summary>
    public IReadOnlyList<UsageLensTarget> VisibleUsageLensTargets()
    {
        var (first, last) = _list.VisibleRange();
        return TargetsIn(first, last);
    }

    private IReadOnlyList<UsageLensTarget> TargetsIn(int firstRow, int lastRow)
    {
        var rows = _rowSet.Rows;
        var from = Math.Max(0, firstRow);
        var to = Math.Min(rows.Count - 1, lastRow);
        if (to < from) return [];

        var targets = new List<UsageLensTarget>();
        for (var i = from; i <= to; i++)
            if (rows[i] is DiffRow.Lens lens) targets.Add(TargetOf(lens));
        return targets;
    }

    private static UsageLensTarget TargetOf(DiffRow.Lens lens) =>
        new(lens.Id, lens.At, lens.NameLine, lens.NameColumn);

    /// <summary>The marked identifier as this row's painter counts columns, or null when the mark
    /// is on another line. Raw columns become expanded ones here, the same conversion the squiggles
    /// make, so a link over a tabbed line lands on its glyphs rather than beside them.</summary>
    private CharRange? LinkOnRow(int rowIndex)
    {
        if (_definitionLink is not { } link) return null;
        if (_rowSet.Rows[rowIndex] is not DiffRow.Line line) return null;
        if (_rowSet.NewLineAt(new RowIndex(rowIndex)) is not { } fileLine || fileLine != link.Line)
            return null;

        var left = line.Text.ToExpanded(link.Start);
        var right = line.Text.ToExpanded(link.End);
        return right <= left ? null : new CharRange(left.Value, right.Value - left.Value);
    }

    private IReadOnlyList<SearchMark>? SearchOnRow(int rowIndex)
    {
        if (!_searchApplies) return null;
        if (_rowSet.Rows[rowIndex] is not DiffRow.Line line) return null;
        if (_rowSet.NewLineAt(new RowIndex(rowIndex)) is not { } fileLine) return null;

        var marks = _search.MarksOn(fileLine, line.Text);
        return marks.Count == 0 ? null : marks;
    }

    private IReadOnlyList<DiagnosticMark>? MarksOnRow(int rowIndex)
    {
        if (_diagnostics.IsEmpty) return null;
        if (_rowSet.Rows[rowIndex] is not DiffRow.Line line) return null;
        if (_rowSet.NewLineAt(new RowIndex(rowIndex)) is not { } fileLine) return null;

        var marks = _diagnostics.MarksOn(fileLine, line.Text);
        return marks.Count == 0 ? null : marks;
    }

    private int ButtonRowFor(int hunkIndex)
    {
        if (hunkIndex < 0 || hunkIndex >= _rowSet.HunkRanges.Count) return -1;
        return HunkButtonBar.ButtonRowFor(_rowSet.HunkRanges[hunkIndex]);
    }

    private bool HasHunkButtons()
        => _hunksPatchable && HunkButtonBar.ActionsFor(_diffSide).Length > 0;

    // WorkingTree pills follow each hunk's real index state once the VM's async pass lands.
    private HunkAction[] ActionsForHunk(int hunkIndex)
        => HunkButtonBar.ActionsFor(_hunkStates, hunkIndex, _diffSide);

    private void DrawHunkOutlineForRow(ICanvas c, RectF rowRect, int rowIndex, int hunkIndex, int z)
    {
        if (hunkIndex < 0 || hunkIndex >= _rowSet.HunkRanges.Count) return;
        var range = _rowSet.HunkRanges[hunkIndex];

        // Left + right edges on every row of the hunk.
        var left = rowRect.Left;
        var right = rowRect.Right - HunkOutlineThickness;
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(left, rowRect.Bottom, HunkOutlineThickness, rowRect.Height),
            Style = new RectStyle { BackgroundColor = _styles.HunkOutline },
            ZIndex = z,
        });
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(right, rowRect.Bottom, HunkOutlineThickness, rowRect.Height),
            Style = new RectStyle { BackgroundColor = _styles.HunkOutline },
            ZIndex = z,
        });

        // Top edge on the header row, bottom edge on the last row.
        if (rowIndex == range.FirstRow)
        {
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(left, rowRect.Top - HunkOutlineThickness, rowRect.Width, HunkOutlineThickness),
                Style = new RectStyle { BackgroundColor = _styles.HunkOutline },
                ZIndex = z,
            });
        }
        if (rowIndex == range.LastRow)
        {
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(left, rowRect.Bottom, rowRect.Width, HunkOutlineThickness),
                Style = new RectStyle { BackgroundColor = _styles.HunkOutline },
                ZIndex = z,
            });
        }
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
        SetExpanderHover(HitTestExpander(point)?.Row ?? -1);
        SetFoldHover(HitTestFold(point)?.Row ?? -1);
        SetLensHover(HitTestLens(point)?.Row ?? -1);

        if (!HasHunkButtons()) { SetHunkHover(-1, HunkAction.None); return; }

        var listPos = _list.Position;
        if (!listPos.ContainsPoint(point)) { SetHunkHover(-1, HunkAction.None); return; }

        var rowIndex = HitTestListRow(point);
        var hunkIndex = _rowSet.HunkIndexOf(rowIndex);
        var button = HunkAction.None;
        if (hunkIndex >= 0)
            button = HitTestButton(point, hunkIndex);
        SetHunkHover(hunkIndex, button);
    }

    public void OnHunkPointerExit()
    {
        SetExpanderHover(-1);
        SetFoldHover(-1);
        SetLensHover(-1);
        SetHunkHover(-1, HunkAction.None);
    }

    public bool TryClickExpander(PointF point, InputModifiers modifiers = InputModifiers.None)
    {
        if (HitTestExpander(point) is not { } hit) return false;
        // Unfold-all already means "all of it", so the modifier only changes what a stepping
        // chevron counts as one step.
        var handler = modifiers.HasFlag(InputModifiers.Alt) && hit.Dir != GapExpandDirection.All
            ? OnExpandGapToDeclaration ?? OnExpandGap
            : OnExpandGap;
        handler?.Invoke(hit.GapIndex, hit.Dir);
        return true;
    }

    public bool TryClickFold(PointF point)
    {
        if (HitTestFold(point) is not { } hit) return false;
        OnToggleFold?.Invoke(hit.Id);
        return true;
    }

    // Two targets for one fold: the chevron in the margin, and the pill standing in for the body
    // it swallowed — which is the one a reader reaches for, because it is the thing they can see.
    private (int Row, string Id)? HitTestFold(PointF point)
    {
        if (!_rowSet.FoldColumn || _lineHeight <= 0) return null;
        var listPos = _list.Position;
        if (!listPos.ContainsPoint(point)) return null;

        var rowIndex = HitTestListRow(point);
        if (rowIndex < 0) return null;
        if (_rowSet.Rows[rowIndex] is not DiffRow.Line { Fold: { } fold } line) return null;

        var contentLeft = listPos.Left - _scrollX;
        if (fold.Chevron
            && DiffRowPainter.FoldHit(point.X - contentLeft, _gutterWidth, _rowSet.SingleGutter))
            return (rowIndex, fold.Id);

        if (!fold.Chip) return null;
        var textLeft = DiffRowPainter.LineTextOriginX(
            contentLeft, _gutterWidth, _rowSet.SingleGutter, _rowSet.FoldColumn, _rowSet.GlyphColumn);
        var (chipX, chipWidth) = _painter.FoldChipBounds(line, textLeft);
        return point.X >= chipX && point.X <= chipX + chipWidth ? (rowIndex, fold.Id) : null;
    }

    private void SetFoldHover(int rowIndex)
    {
        if (_hoveredFoldRow == rowIndex) return;
        _hoveredFoldRow = rowIndex;
        SetDirty();
    }

    public bool TryClickLens(PointF point)
    {
        if (HitTestLens(point) is not { } hit) return false;
        UsageLensActivated?.Invoke(hit.Target, hit.Anchor);
        return true;
    }

    // Only the words are the target, not the whole row: a lens row spans the file's full width and
    // clicking the empty margin beside one should still start a selection.
    private (int Row, UsageLensTarget Target, PointF Anchor)? HitTestLens(PointF point)
    {
        if (_lineHeight <= 0 || _usageLens.IsEmpty) return null;
        if (!_list.Position.ContainsPoint(point)) return null;

        var rowIndex = HitTestListRow(point);
        if (rowIndex < 0 || _rowSet.Rows[rowIndex] is not DiffRow.Lens lens) return null;
        if (_usageLens.On(lens.At) is not { } state) return null;

        var (x, width) = _painter.LensBounds(lens, _painter.LensLabel(state), LineTextOriginX());
        if (width <= 0f || point.X < x || point.X > x + width) return null;

        // Anchored to the words rather than to the pixel clicked, so what opens hangs off the lens
        // the way it does off a menu, wherever in it the reader happened to click.
        var anchor = _list.TryGetRowRect(rowIndex, out var rect)
            ? new PointF(x, rect.Bottom)
            : point;
        return (rowIndex, TargetOf(lens), anchor);
    }

    private void SetLensHover(int rowIndex)
    {
        if (_hoveredLensRow == rowIndex) return;
        _hoveredLensRow = rowIndex;
        SetDirty();
    }

    // Where a row's text starts, in the same content space every hit-test measures against.
    private float LineTextOriginX() => DiffRowPainter.LineTextOriginX(
        _list.Position.Left - _scrollX, _gutterWidth, _rowSet.SingleGutter, _rowSet.FoldColumn,
        _rowSet.GlyphColumn);

    private (int Row, int GapIndex, GapExpandDirection Dir)? HitTestExpander(PointF point)
    {
        if (_lineHeight <= 0) return null;
        var listPos = _list.Position;
        if (!listPos.ContainsPoint(point)) return null;
        var rowIndex = HitTestListRow(point);
        if (rowIndex < 0 || DiffRowPainter.GapBarOf(_rowSet.Rows[rowIndex]) is not { } gap) return null;

        var contentLeft = listPos.Left - _scrollX;
        if (DiffRowPainter.ExpanderHit(gap, point.X - contentLeft) is not { } dir) return null;
        return (rowIndex, gap.GapIndex, dir);
    }

    private void SetExpanderHover(int rowIndex)
    {
        if (_hoveredExpanderRow == rowIndex) return;
        _hoveredExpanderRow = rowIndex;
        SetDirty();
    }

    public bool TryClickHunkAction(PointF point)
    {
        if (!HasHunkButtons()) return false;
        var listPos = _list.Position;
        if (!listPos.ContainsPoint(point)) return false;

        var rowIndex = HitTestListRow(point);
        var hunkIndex = _rowSet.HunkIndexOf(rowIndex);
        if (hunkIndex < 0) return false;

        var button = HitTestButton(point, hunkIndex);
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

    private int HitTestListRow(PointF point) => _lineHeight <= 0 ? -1 : _list.RowIndexAt(point);

    // The row a drag that has wandered off the rows should keep extending to. The widget answers
    // only for points actually over a row, so the pointer is pulled onto the nearest edge of the
    // viewport first and the ends are named outright — including the empty band below a file
    // shorter than the viewport. A drag held past an edge then travels further under the drag
    // auto-scroll rather than by naming a row that isn't on screen.
    private int DragRowIndexAt(PointF point)
    {
        var pos = _list.Position;
        var index = _list.RowIndexAt(new PointF(
            Math.Clamp(point.X, pos.Left, pos.Right),
            Math.Clamp(point.Y, pos.Bottom, pos.Top)));
        if (index >= 0) return index;
        return _list.TryGetRowRect(0, out var first) && point.Y > first.Top ? 0 : _rowSet.Rows.Count - 1;
    }

    // ---- text selection ----

    // One file, so every position shares the single implicit scope: null.
    DiffSelectionModel IDiffSelectionSurface.Selection => _selection;
    RectF IDiffSelectionSurface.SelectionViewport => _list.Position;
    IReadOnlyList<DiffRow>? IDiffSelectionSurface.RowsOf(object? scope) => _rowSet.Rows;
    Func<RowIndex, string?>? IDiffSelectionSurface.HiddenTextOf(object? scope) =>
        _rowSet.FoldColumn ? _rowSet.HiddenAfter : null;
    void IDiffSelectionSurface.ScrollBy(float dy) => _list.SetScrollY(_list.ScrollY + dy);
    void IDiffSelectionSurface.RequestRedraw() => SetDirty();

    bool IDiffSelectionSurface.ShowSelectionMenu(PointF point)
    {
        if (!AssistantActions || _bus is null) return false;
        if (DescribeState(_renderState).Path is not { } path) return false;
        if (DiffSelectionQuote.Build(
                _rowSet.Rows, _selection.Start, _selection.End, path, AnnotationsOf(_renderState)) is not { } quote)
            return false;

        return RepoBarContextMenu.Show(_ctx, point, DiffAssistantMenu.Items(_loc.Strings.Value, _bus, quote)) != null;
    }

    private static DiffAnnotations? AnnotationsOf(DiffRenderState state) => state switch
    {
        DiffRenderState.Loaded loaded => loaded.Annotations,
        DiffRenderState.FullFile fullFile => fullFile.Annotations,
        _ => null,
    };

    bool IDiffSelectionSurface.IsInteractiveAt(PointF point)
    {
        if (HitTestExpander(point) != null) return true;
        if (HitTestFold(point) != null) return true;
        if (HitTestLens(point) != null) return true;
        if (!HasHunkButtons()) return false;
        var hunkIndex = _rowSet.HunkIndexOf(HitTestListRow(point));
        return hunkIndex >= 0 && HitTestButton(point, hunkIndex) != HunkAction.None;
    }

    DiffTextHit? IDiffSelectionSurface.HitTestText(PointF point)
    {
        if (_lineHeight <= 0 || !_list.Position.ContainsPoint(point)) return null;
        var rowIndex = HitTestListRow(point);
        if (rowIndex < 0 || _rowSet.Rows[rowIndex] is not DiffRow.Line line) return null;
        return new DiffTextHit(
            null, new DiffTextPos(new RowIndex(rowIndex), CharIndexAt(line.Text.Expanded, point.X)));
    }

    /// <summary>
    /// The place in the file under a pixel: a line as the file counts them, and an offset into that
    /// line's own characters rather than into the tabs-expanded text on screen. Null over anything
    /// that is not a line of the after-side file — a banner, a hunk bar, a removed line.
    /// </summary>
    /// <remarks>
    /// The two conversions this performs are the ones a language server's answers depend on, and
    /// both are silent when wrong: a row is not a line number, and a screen column is not a file
    /// column wherever the line contains a tab.
    /// </remarks>
    public FilePositionHit? HitTestFilePosition(PointF point) => FilePositionUnder(point)?.At;

    /// <summary>The identifier under a pixel, for the link a held modifier draws over one. Null
    /// wherever <see cref="HitTestFilePosition"/> is, and also over punctuation and whitespace.</summary>
    public FileSpan? HitTestIdentifier(PointF point)
    {
        if (FilePositionUnder(point) is not { } under) return null;
        // The glyph under the pointer, not the caret position nearest it: a link covers the word
        // it is drawn over and nothing either side of it, so the whitespace between two words has
        // to belong to neither.
        var expanded = DiffText.CharIndexOnCell(under.Text.Expanded, CellAt(point.X));
        if (expanded < 0) return null;
        var column = under.Text.ToRaw(new ExpandedColumn(expanded), TabEdge.Before);
        if (under.Text.IdentifierAt(column) is not { } span) return null;
        return new FileSpan(under.At.Line, span.Start, span.End);
    }

    /// <summary>Marks an identifier as somewhere the reader can click through to, or clears the
    /// mark. Held here rather than recomputed per frame because it outlives the event that set it:
    /// the pointer stops moving while a modifier goes down and comes back up.</summary>
    public void ShowDefinitionLink(FileSpan? link)
    {
        if (_definitionLink == link) return;
        _definitionLink = link;
        SetDirty();
    }

    private (DiffLineText Text, FilePositionHit At)? FilePositionUnder(PointF point)
    {
        if (_lineHeight <= 0 || !_list.Position.ContainsPoint(point)) return null;

        var rowIndex = HitTestListRow(point);
        if (rowIndex < 0 || _rowSet.Rows[rowIndex] is not DiffRow.Line line) return null;
        if (_rowSet.NewLineAt(new RowIndex(rowIndex)) is not { } fileLine) return null;

        var column = line.Text.ToRaw(CharIndexAt(line.Text.Expanded, point.X), TabEdge.Before);
        return (line.Text, new FilePositionHit(fileLine, column));
    }

    DiffTextHit? IDiffSelectionSurface.ClampToScope(PointF point, object? scope)
    {
        if (_lineHeight <= 0 || _rowSet.Rows.Count == 0) return null;
        var rowIndex = DragRowIndexAt(point);
        // A drag crossing a banner or a hunk bar keeps extending through it; those rows carry no
        // selectable text, so they contribute nothing to the copy.
        var text = _rowSet.Rows[rowIndex] is DiffRow.Line line ? line.Text.Expanded : string.Empty;
        return new DiffTextHit(null, new DiffTextPos(new RowIndex(rowIndex), CharIndexAt(text, point.X)));
    }

    private ExpandedColumn CharIndexAt(string text, float x) =>
        _monoAdvance <= 0 ? default : new ExpandedColumn(DiffText.CharIndexAtCell(text, CellAt(x)));

    /// <summary>The monospace cell an x falls in, unclamped — negative over the gutters, past the
    /// line's own cell count out in the margin.</summary>
    private float CellAt(float x)
    {
        if (_monoAdvance <= 0) return 0f;
        var origin = DiffRowPainter.LineTextOriginX(
            _list.Position.Left - _scrollX, _gutterWidth, _rowSet.SingleGutter, _rowSet.FoldColumn,
            _rowSet.GlyphColumn);
        return (x - origin) / _monoAdvance;
    }

    private MouseCursor CursorAt(PointF point)
    {
        if (LinkCovers(point)) return MouseCursor.Hand;
        if (((IDiffSelectionSurface)this).IsInteractiveAt(point)) return MouseCursor.Hand;
        return ((IDiffSelectionSurface)this).HitTestText(point) != null
            ? MouseCursor.Text
            : MouseCursor.Default;
    }

    // The link was computed for one pixel and then held, so that pixel can stop being over it —
    // a scroll slides the line out from under a cursor that never moved. The cursor shape asks
    // where the pointer is now rather than trusting the mark.
    private bool LinkCovers(PointF point) =>
        _definitionLink is { } link &&
        HitTestFilePosition(point) is { } at &&
        at.Line == link.Line &&
        at.Column.Value >= link.Start.Value &&
        at.Column.Value <= link.End.Value;

    private HunkAction HitTestButton(PointF point, int hunkIndex)
    {
        var buttonRowIndex = ButtonRowFor(hunkIndex);
        if (buttonRowIndex < 0) return HunkAction.None;
        if (!_list.TryGetRowRect(buttonRowIndex, out var rowRect)) return HunkAction.None;
        return _buttonBar.HitTest(point, _list.Position.Right, rowRect.Top, ActionsForHunk(hunkIndex));
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
}
