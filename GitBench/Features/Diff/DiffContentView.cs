using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Input;
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

internal enum HunkAction { None, Stage, Unstage, Discard }

/// <summary>What <see cref="DiffContentView"/> needs from whatever produced its rows.</summary>
internal interface IDiffRowSource
{
    IReadOnlyList<DiffRow> Rows { get; }

    /// <summary>The widest row in monospace cells, which the horizontal extent is sized from.</summary>
    int MaxRowCells { get; }

    /// <summary>Max line-number digit count across the gutters, for gutter width sizing.</summary>
    int GutterDigits { get; }

    /// <summary>One line-number gutter rather than the diff's old|new pair.</summary>
    bool SingleGutter { get; }

    /// <summary>Whether rows reserve the fold chevron column.</summary>
    bool FoldColumn { get; }

    /// <summary>Whether rows reserve the +/- glyph column.</summary>
    bool GlyphColumn { get; }

    /// <summary>The after-side file line a row stands for, or null where it stands for none.</summary>
    FileLine? NewLineAt(RowIndex row);

    /// <summary>Where to scroll for an after-side file line — its own row, or the closest numbered
    /// one above it. Null when nothing precedes it either.</summary>
    RowIndex? RowNearestNewLine(FileLine line);

    /// <summary>Where a row sits, in terms that survive this stream being rebuilt.</summary>
    DiffRowAnchor? AnchorAt(RowIndex row);

    /// <summary>The row an anchor names here, or null when this stream does not have it.</summary>
    RowIndex? RowAt(DiffRowAnchor anchor);

    /// <summary>The text a collapsed fold swallowed after a row, or null on a stream that does not fold.</summary>
    Func<RowIndex, string?>? HiddenText { get; }

    /// <summary>The hunks these rows are grouped into, or null for a stream that has none.</summary>
    IDiffHunkRows? Hunks { get; }
}

/// <summary>What a diff body is showing rows out of: a flattened render, or a document being edited.</summary>
internal abstract record DiffBody(IDiffRowSource Rows)
{
    public sealed record Viewer(DiffRowSet Set) : DiffBody(Set);

    public sealed record Edited(Features.Editor.EditorBuffer Buffer) : DiffBody(Buffer.Rows);
}

/// <summary>The hunk chrome a diff row stream carries: which hunk owns a row, and each hunk's row span.</summary>
internal interface IDiffHunkRows
{
    IReadOnlyList<HunkRowRange> Ranges { get; }

    /// <summary>The hunk owning a flattened row, or -1 for chrome rows.</summary>
    int HunkIndexOf(int rowIndex);
}

internal sealed class DiffContentView : View, IScrollableContent, IDiffSelectionSurface,
    IScrollScope, Features.Editor.IEditorSurface,
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
    private DiffBody _body = new DiffBody.Viewer(DiffRowSet.Empty);

    private IDiffRowSource RowSource => _body.Rows;

    private Features.Editor.EditorBuffer? Document =>
        _body is DiffBody.Edited edited ? edited.Buffer : null;
    private float _caretPhase;
    private bool _focused;
    private DiffTextPos _lastCaret;
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
    private readonly Features.Editor.EditorController _editorController;
    private readonly IClipboard? _clipboard;
    private readonly Features.Editor.DocumentSaves? _saves;

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
        this.UseController(input, () => new DiffMouseController(this), EventPhaseFilter.Capture);
        _clipboard = ctx.Get<IClipboard>();
        _saves = Features.Editor.DocumentSaves.From(ctx);
        _editorController = new Features.Editor.EditorController(this, input, ctx.KeyMap());
        _selectionController = new DiffSelectionController(this, input, _clipboard, _editorController);
        this.UseController(input, _selectionController, EventPhaseFilter.Both);

        if (ctx.Get<IFrameTicker>() is { } ticker) UseCaretBlink(ticker);

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

    public void SetRenderState(DiffRenderState state, Features.Editor.EditorBuffer? document)
    {
        // Capture the outgoing view's identity and position before rebuilding rows, so we can
        // preserve the reading position across a mode toggle and hold it across the async
        // highlight re-emit that follows. _renderState still holds the previous state here.
        var (prevPath, prevWasFullFile) = DescribeState(_renderState);
        var prevTopLine = TopVisibleNewLine();
        var prevScrollY = _list.ScrollY;
        var prevScrollX = _scrollX;
        var remap = SelectionRemap();

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

        _body = document is null
            ? new DiffBody.Viewer(DiffRowSet.Build(state, _loc, FoldsFor(state), _usageLensRows))
            : Opened(document, state);
        if (state is DiffRenderState.Loaded loaded)
        {
            _diffSide = loaded.Result.Side;
            _hunksPatchable = HunkPatchBuilder.CanPatchHunk(loaded.Result);
        }
        else if (state is DiffRenderState.FullFile fullFile)
        {
            _diffSide = fullFile.Side;
        }
        _gutterWidth = RowSource.GutterDigits * AssumedFontSize * FallbackMonoAdvanceRatio + 8f;

        var (newPath, _) = DescribeState(state);
        if (newPath != prevPath) _selection.Clear();
        else _selection.Remap(remap);
        if (newPath != prevPath) _pendingScrollLine = null;
        // A different file republishes its top line even when the number is unchanged: it is a
        // different declaration at line 1.
        if (newPath != prevPath) _topLinePublished = false;

        RefreshSearchScope();
        _list.ItemCount = RowSource.Rows.Count;
        _list.NotifyItemsChanged();
        ApplyScrollForTransition(state, prevPath, prevWasFullFile, prevTopLine, prevScrollY, prevScrollX);
        _editorController.SyncIme();
        SetDirty();
    }

    private DiffBody Opened(Features.Editor.EditorBuffer document, DiffRenderState state)
    {
        var annotations = AnnotationsOf(state);
        document.FoldExpanded = path => OnToggleFold?.Invoke(path);
        // Through ApplyRead, not Apply: this render state was built from a read of the file on
        // disk, so it describes whatever revision that read found — which is the revision the
        // buffer was opened at only until someone types. Stamping it with the opening revision
        // regardless refuses a re-read that is in fact current, and would overwrite a parse of the
        // buffer with a parse of the file.
        document.ApplyRead(new Features.Editor.EditorAnnotations(annotations?.Highlight, annotations?.NewSide));
        document.SetFolds(FoldsFor(state));
        document.UsageLensRows = _usageLensRows;
        return new DiffBody.Edited(document);
    }

    /// <summary>
    /// Replaces the fold set and re-flattens, deliberately not through <see cref="SetRenderState"/>.
    /// That path resets horizontal scroll, and restores a *pixel* offset — which after a collapse
    /// above the viewport would silently move the reader onto a different line. This one re-anchors
    /// on the line they were reading instead.
    /// </summary>
    public void SetFoldState(FoldState folds)
    {
        _foldState = folds;
        if (_renderState is not DiffRenderState.FullFile) { SetDirty(); return; }

        var topLine = TopVisibleNewLine();
        var remap = SelectionRemap();
        if (Document is { } document) document.SetFolds(FoldsFor(_renderState));
        else _body = new DiffBody.Viewer(
            DiffRowSet.Build(_renderState, _loc, FoldsFor(_renderState), _usageLensRows));
        _selection.Remap(remap);
        _hoveredFoldRow = -1;
        _hoveredLensRow = -1;
        _list.ItemCount = RowSource.Rows.Count;
        _list.NotifyItemsChanged();
        if (topLine is { } line) ScrollToNewLine(line, leadIn: 0);
        _editorController.SyncIme();
        SetDirty();
    }

    /// <summary>How the selection's endpoints read once the rows are rebuilt. Named before anything
    /// reshapes them: an editable projection folds and reparses in place.</summary>
    private Func<DiffTextPos, DiffTextPos?> SelectionRemap()
    {
        var outgoing = RowSource;
        var at = _selection.Anchor;
        var anchor = outgoing.AnchorAt(at.Row);
        var focus = outgoing.AnchorAt(_selection.Focus.Row);

        return pos => (pos == at ? anchor : focus) is { } named && RowSource.RowAt(named) is { } row
            ? new DiffTextPos(row, pos.Char)
            : null;
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
        var count = RowSource.Rows.Count;
        if (_lineHeight <= 0 || count == 0) return null;
        var topIndex = Math.Clamp(_list.VisibleRange().First, 0, count - 1);
        for (var i = topIndex; i < count; i++)
            if (RowSource.NewLineAt(new RowIndex(i)) is { } line) return line;
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

    /// <summary>Whether what was computed from the file as it was read still describes what is on
    /// screen — the find bar's hits, and the server's diagnostics.</summary>
    private bool ReadStillDescribesTheDocument => Document is not { ReadIsCurrent: false };

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
        if (_pendingSearchReveal is not { } match) return;
        // Dropped rather than held: a hit found in text the document has since moved past names a
        // line that has moved with it, and holding it reveals that line whenever editing stops.
        if (!_searchApplies || !ReadStillDescribesTheDocument) { _pendingSearchReveal = null; return; }
        if (_lineHeight <= 0) return;
        _pendingSearchReveal = null;

        if (RowSource.RowNearestNewLine(match.Line) is not { } row) return;
        if (RowSource.Rows[row.Value] is not DiffRow.Line line) return;
        if (!_list.TryGetRowRect(row.Value, out var rowRect)) return;
        EnsureVisible(SpanRect(line.Text, match, rowRect));
    }

    private RectF SpanRect(DiffLineText text, FileSearchMatch match, RectF rowRect)
    {
        var origin = DiffRowPainter.LineTextOriginX(
            _list.Position.Left - _scrollX, _gutterWidth, RowSource.SingleGutter, RowSource.FoldColumn,
            RowSource.GlyphColumn);
        var left = origin + DiffText.CellsBefore(text.Expanded, text.ToExpanded(match.Start).Value) * _monoAdvance;
        var right = origin + DiffText.CellsBefore(text.Expanded, text.ToExpanded(match.End).Value) * _monoAdvance;
        return new RectF(left, rowRect.Bottom, Math.Max(0f, right - left), rowRect.Height);
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
        if (RowSource.RowNearestNewLine(line) is not { } row) return;
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
        var rows = RowSource.Rows;
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
        var gutters = RowSource.SingleGutter ? _gutterWidth : _gutterWidth + _gutterWidth;
        var lineWidth = DiffRowPainter.MarkerLaneWidth
            + gutters + DiffRowPainter.FoldColumnWidthOf(RowSource.FoldColumn)
            + DiffRowPainter.GlyphColumnWidthOf(RowSource.GlyphColumn)
            + RowSource.MaxRowCells * _monoAdvance + DiffRowPainter.BannerPaddingX;
        var bannerWidth = DiffRowPainter.BannerPaddingX * 2 + RowSource.MaxRowCells * _monoAdvance;
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
        _gutterWidth = RowSource.GutterDigits * _monoAdvance + 8f;
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
            case DiffRenderState.Loaded when RowSource.Rows.Count == 0:
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
        NoteCaretMoved();
        // After the geometry above and before the child list draws its rows.
        _editorController.SyncIme();
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
        var rows = RowSource.Rows;
        if (rowIndex < 0 || rowIndex >= rows.Count) return;

        // Apply horizontal scroll inside the widget's row rect. Vertical position comes
        // from the widget; horizontal position is our concern.
        var rowLeft = rowRect.Left - _scrollX;
        var rowWidth = ContentWidth();

        var hunkIndex = HunkIndexOf(rowIndex);
        var isHoveredHunk = hunkIndex >= 0 && hunkIndex == _hoveredHunkIndex;
        var showButtons = isHoveredHunk && rowIndex == ButtonRowFor(hunkIndex) && HasHunkButtons();

        var composing = ComposedOn(rowIndex);
        var drawn = composing?.Line ?? rows[rowIndex];

        DiffRowSelection? selection = null;
        if (drawn is DiffRow.Line line
            && _selection.TryRowSpan(null, new RowIndex(rowIndex), line.Text.End, out var span))
            selection = composing is { } shift ? Shifted(span, shift) : span;

        _painter.DrawRow(c, drawn, new DiffRowPaint(
            rowLeft, rowRect.Bottom, rowWidth, _gutterWidth, RowSource.SingleGutter,
            ExpanderHovered: rowIndex == _hoveredExpanderRow,
            Viewport: _list.Position,
            Z: z,
            Selection: selection,
            FoldColumn: RowSource.FoldColumn,
            FoldHovered: rowIndex == _hoveredFoldRow,
            Diagnostics: composing is null ? MarksOnRow(rowIndex) : null,
            GlyphColumn: RowSource.GlyphColumn,
            Link: composing is null ? LinkOnRow(rowIndex) : null,
            Usages: UsagesOnRow(rowIndex),
            LensHovered: rowIndex == _hoveredLensRow,
            Search: composing is null ? SearchOnRow(rowIndex) : null));

        if (composing is { } preedit) DrawPreeditUnderlines(c, preedit, rowLeft, rowRect, z + 3);

        if (CaretRectOn(rowIndex, rowLeft, rowRect) is { } caret)
            c.DrawRect(new DrawRectInputs
            {
                Position = caret,
                Style = new RectStyle { BackgroundColor = _styles.Caret },
                ZIndex = z + 7,
            });

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
            if (Document is not { } document) { SetRenderState(_renderState, null); return; }

            var remap = SelectionRemap();
            document.UsageLensRows = value;
            _selection.Remap(remap);
            _hoveredLensRow = -1;
            ReconcileRows();
        }
    }

    // What a lens row has to say, or null on every other row and on a declaration nothing has been
    // asked about yet.
    private UsageLensState? UsagesOnRow(int rowIndex) =>
        RowSource.Rows[rowIndex] is DiffRow.Lens lens ? _usageLens.On(lens.At) : null;

    /// <summary>Every declaration carrying a usages row in the current render, in row order.
    /// Empty whenever the rows are off, and for a diff, which never grows them.</summary>
    public IReadOnlyList<UsageLensTarget> UsageLensTargets() => TargetsIn(0, RowSource.Rows.Count - 1);

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
        var rows = RowSource.Rows;
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
        if (RowSource.Rows[rowIndex] is not DiffRow.Line line) return null;
        if (RowSource.NewLineAt(new RowIndex(rowIndex)) is not { } fileLine || fileLine != link.Line)
            return null;

        var left = line.Text.ToExpanded(link.Start);
        var right = line.Text.ToExpanded(link.End);
        return right <= left ? null : new CharRange(left.Value, right.Value - left.Value);
    }

    private IReadOnlyList<SearchMark>? SearchOnRow(int rowIndex)
    {
        if (!_searchApplies || !ReadStillDescribesTheDocument) return null;
        if (RowSource.Rows[rowIndex] is not DiffRow.Line line) return null;
        if (RowSource.NewLineAt(new RowIndex(rowIndex)) is not { } fileLine) return null;

        var marks = _search.MarksOn(fileLine, line.Text);
        return marks.Count == 0 ? null : marks;
    }

    private IReadOnlyList<DiagnosticMark>? MarksOnRow(int rowIndex)
    {
        if (_diagnostics.IsEmpty || !ReadStillDescribesTheDocument) return null;
        if (RowSource.Rows[rowIndex] is not DiffRow.Line line) return null;
        if (RowSource.NewLineAt(new RowIndex(rowIndex)) is not { } fileLine) return null;

        var marks = _diagnostics.MarksOn(fileLine, line.Text);
        return marks.Count == 0 ? null : marks;
    }

    private int HunkIndexOf(int rowIndex) => RowSource.Hunks?.HunkIndexOf(rowIndex) ?? -1;

    private IReadOnlyList<HunkRowRange>? HunkRanges => RowSource.Hunks?.Ranges;

    private int ButtonRowFor(int hunkIndex)
    {
        if (HunkRanges is not { } ranges || hunkIndex < 0 || hunkIndex >= ranges.Count) return -1;
        return HunkButtonBar.ButtonRowFor(ranges[hunkIndex]);
    }

    private bool HasHunkButtons()
        => _hunksPatchable && HunkButtonBar.ActionsFor(_diffSide).Length > 0;

    // WorkingTree pills follow each hunk's real index state once the VM's async pass lands.
    private HunkAction[] ActionsForHunk(int hunkIndex)
        => HunkButtonBar.ActionsFor(_hunkStates, hunkIndex, _diffSide);

    private void DrawHunkOutlineForRow(ICanvas c, RectF rowRect, int rowIndex, int hunkIndex, int z)
    {
        if (HunkRanges is not { } ranges || hunkIndex < 0 || hunkIndex >= ranges.Count) return;
        var range = ranges[hunkIndex];

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
        var hunkIndex = HunkIndexOf(rowIndex);
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
        if (!RowSource.FoldColumn || _lineHeight <= 0) return null;
        var listPos = _list.Position;
        if (!listPos.ContainsPoint(point)) return null;

        var rowIndex = HitTestListRow(point);
        if (rowIndex < 0) return null;
        if (RowSource.Rows[rowIndex] is not DiffRow.Line { Fold: { } fold } line) return null;

        var contentLeft = listPos.Left - _scrollX;
        if (fold.Chevron
            && DiffRowPainter.FoldHit(point.X - contentLeft, _gutterWidth, RowSource.SingleGutter))
            return (rowIndex, fold.Id);

        if (!fold.Chip) return null;
        var textLeft = DiffRowPainter.LineTextOriginX(
            contentLeft, _gutterWidth, RowSource.SingleGutter, RowSource.FoldColumn, RowSource.GlyphColumn);
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
        if (rowIndex < 0 || RowSource.Rows[rowIndex] is not DiffRow.Lens lens) return null;
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
        _list.Position.Left - _scrollX, _gutterWidth, RowSource.SingleGutter, RowSource.FoldColumn,
        RowSource.GlyphColumn);

    private (int Row, int GapIndex, GapExpandDirection Dir)? HitTestExpander(PointF point)
    {
        if (_lineHeight <= 0) return null;
        var listPos = _list.Position;
        if (!listPos.ContainsPoint(point)) return null;
        var rowIndex = HitTestListRow(point);
        if (rowIndex < 0 || DiffRowPainter.GapBarOf(RowSource.Rows[rowIndex]) is not { } gap) return null;

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
        var hunkIndex = HunkIndexOf(rowIndex);
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
        return _list.TryGetRowRect(0, out var first) && point.Y > first.Top ? 0 : RowSource.Rows.Count - 1;
    }

    // ---- caret ----

    private const float CaretWidth = 2f;

    private const float CaretBlinkSeconds = 1.06f;

    private bool HasCaret =>
        Document != null && _focused && _selection.IsActive && _monoAdvance > 0;

    private bool CaretDrawn => HasCaret && _caretPhase < CaretBlinkSeconds / 2f;

    Features.Editor.EditorBuffer? Features.Editor.IEditorSurface.Editor => Document;
    DiffSelectionModel Features.Editor.IEditorSurface.Selection => _selection;
    object? Features.Editor.IEditorSurface.SelectionScope => null;
    void Features.Editor.IEditorSurface.RequestRedraw() => SetDirty();
    bool Features.Editor.IEditorSurface.CopySelection() => _selectionController.Copy();
    bool Features.Editor.IEditorSurface.SelectAllText() => _selectionController.SelectAll();
    string? Features.Editor.IEditorSurface.ClipboardText() => _clipboard?.GetText();

    void Features.Editor.IEditorSurface.RequestSave()
    {
        if (Document is { } editor) _saves?.Save(editor.Path, editor.Document, editor.Encoding);
    }

    void Features.Editor.IEditorSurface.RowsChanged() => ReconcileRows();

    Features.Editor.ImeCaret Features.Editor.IEditorSurface.Caret =>
        HasCaret
            ? new Features.Editor.ImeCaret.At(_selection.Focus, CaretRect())
            : Features.Editor.ImeCaret.Nowhere;

    private void ReconcileRows()
    {
        if (RowSource.Rows.Count != _list.ItemCount)
        {
            _list.ItemCount = RowSource.Rows.Count;
            _list.NotifyItemsChanged();
        }

        var advance = _monoAdvance > 0 ? _monoAdvance : AssumedFontSize * FallbackMonoAdvanceRatio;
        _gutterWidth = RowSource.GutterDigits * advance + 8f;
        SetDirty();
    }

    int Features.Editor.IEditorSurface.PageRows =>
        _lineHeight <= 0 ? 1 : Math.Max(1, (int)(_list.Position.Height / _lineHeight) - 1);

    void Features.Editor.IEditorSurface.RevealCaret()
    {
        // Before the rect is measured: a caret steered into a collapsed declaration opens it.
        ReconcileRows();
        if (CaretRect() is { } rect) EnsureVisible(rect);
    }

    /// <summary>Brings a rect of the drawn content into view on both axes, and only as far as it
    /// has to, in the coordinates the rect was drawn in.</summary>
    public void EnsureVisible(RectF rect)
    {
        var view = _list.Position;
        if (_lineHeight > 0 && view.Height > 0)
        {
            if (rect.Top > view.Top) SetScrollTarget(_list.ScrollY + view.Top - rect.Top);
            else if (rect.Bottom < view.Bottom) SetScrollTarget(_list.ScrollY + view.Bottom - rect.Bottom);
        }

        if (_monoAdvance <= 0 || view.Width <= 0) return;

        var margin = RevealMarginCells * _monoAdvance;
        var prev = _scrollX;
        if (rect.Left - margin < view.Left) _scrollX -= view.Left - rect.Left + margin;
        else if (rect.Right + margin > view.Right) _scrollX += rect.Right - view.Right + margin;
        ClampHorizontalScroll();
        if (_scrollX == prev) return;
        SetDirty();
        NotifyScrollChanged(viewportFits: false);
    }

    private RectF? CaretRectOn(int rowIndex, float rowLeft, RectF rowRect) =>
        CaretDrawn && _selection.Focus.Row.Value == rowIndex
            ? CaretRectAt(rowIndex, rowLeft, rowRect)
            : null;

    private RectF? CaretRect()
    {
        if (!HasCaret) return null;
        var row = _selection.Focus.Row.Value;
        return _list.TryGetRowRect(row, out var rowRect)
            ? CaretRectAt(row, _list.Position.Left - _scrollX, rowRect)
            : null;
    }

    private RectF? CaretRectAt(int rowIndex, float rowLeft, RectF rowRect)
    {
        if (rowIndex < 0 || rowIndex >= RowSource.Rows.Count) return null;
        var composing = ComposedOn(rowIndex);
        if ((composing?.Line ?? RowSource.Rows[rowIndex]) is not DiffRow.Line line) return null;

        var column = composing?.Caret ?? _selection.Focus.Char;
        var origin = DiffRowPainter.LineTextOriginX(
            rowLeft, _gutterWidth, RowSource.SingleGutter, RowSource.FoldColumn, RowSource.GlyphColumn);
        var cells = DiffText.CellsBefore(line.Text.Expanded, column.Value);
        return new RectF(origin + cells * _monoAdvance, rowRect.Bottom, CaretWidth, rowRect.Height);
    }

    // ---- composition ----

    private const float PreeditUnderlineInset = 1f;
    private const float PreeditUnderlineThickness = 1f;
    private const float PreeditFocusedUnderlineThickness = 2f;

    /// <summary>The caret's row with the in-flight composition spliced into it, and the columns
    /// everything that measures it needs.</summary>
    private sealed record ComposedRow(
        int Row,
        DiffLineText Source,
        Features.Editor.ImeComposition Of,
        DiffRow.Line Line,
        ExpandedColumn Start,
        ExpandedColumn End,
        ExpandedColumn Caret,
        int RawStart);

    private ComposedRow? _composed;

    private ComposedRow? ComposedOn(int rowIndex) =>
        Composed() is { } composed && composed.Row == rowIndex ? composed : null;

    private ComposedRow? Composed()
    {
        if (_editorController.Composition is not { } composition)
        {
            _composed = null;
            return null;
        }

        var rows = RowSource.Rows;
        var row = composition.At.Row.Value;
        if (row < 0 || row >= rows.Count || rows[row] is not DiffRow.Line line) return null;

        if (_composed is { } cached
            && cached.Row == row
            && ReferenceEquals(cached.Source, line.Text)
            && cached.Of == composition)
            return cached;

        return _composed = Splice(row, line, composition);
    }

    private static ComposedRow Splice(int row, DiffRow.Line line, Features.Editor.ImeComposition composition)
    {
        var preedit = composition.Preedit.Text;
        var raw = line.Text.Raw;
        var at = Math.Clamp(line.Text.ToRaw(composition.At.Char, TabEdge.Before).Value, 0, raw.Length);
        var text = DiffLineText.Of(string.Concat(raw.AsSpan(0, at), preedit, raw.AsSpan(at)));

        var start = text.ToExpanded(new RawColumn(at));
        var end = text.ToExpanded(new RawColumn(at + preedit.Length));
        var caret = text.ToExpanded(
            new RawColumn(at + Math.Clamp(composition.Preedit.Caret, 0, preedit.Length)));

        return new ComposedRow(
            row, line.Text, composition,
            line with { Text = text, Spans = Shifted(line.Spans, start.Value, end.Value - start.Value) },
            start, end, caret, at);
    }

    private static IReadOnlyList<TokenSpan>? Shifted(IReadOnlyList<TokenSpan>? spans, int at, int width)
    {
        if (spans is not { Count: > 0 } || width <= 0) return spans;

        var shifted = new TokenSpan[spans.Count];
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            var start = span.Start >= at ? span.Start + width : span.Start;
            var stop = span.Start + span.Length > at ? span.Start + span.Length + width : span.Start + span.Length;
            shifted[i] = span with { Start = start, Length = Math.Max(0, stop - start) };
        }
        return shifted;
    }

    private static DiffRowSelection Shifted(in DiffRowSelection span, ComposedRow composing)
    {
        var at = composing.Start.Value;
        var width = composing.End.Value - at;
        return span with
        {
            StartChar = new ExpandedColumn(
                span.StartChar.Value >= at ? span.StartChar.Value + width : span.StartChar.Value),
            EndChar = new ExpandedColumn(
                span.EndChar.Value > at ? span.EndChar.Value + width : span.EndChar.Value),
        };
    }

    private void DrawPreeditUnderlines(ICanvas c, ComposedRow composing, float rowLeft, RectF rowRect, int z)
    {
        var expanded = composing.Line.Text.Expanded;
        var origin = DiffRowPainter.LineTextOriginX(
            rowLeft, _gutterWidth, RowSource.SingleGutter, RowSource.FoldColumn, RowSource.GlyphColumn);
        var y = rowRect.Bottom + PreeditUnderlineInset;

        var blocks = composing.Of.Preedit.Blocks;
        if (blocks.Count == 0)
        {
            DrawPreeditRule(c, expanded, origin, y, composing.Start, composing.End,
                PreeditUnderlineThickness, z);
            return;
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var text = composing.Line.Text;
            var from = text.ToExpanded(new RawColumn(composing.RawStart + block.Start));
            var to = text.ToExpanded(new RawColumn(composing.RawStart + block.Start + block.Length));
            DrawPreeditRule(c, expanded, origin, y, from, to,
                i == composing.Of.Preedit.FocusedBlock
                    ? PreeditFocusedUnderlineThickness
                    : PreeditUnderlineThickness,
                z);
        }
    }

    private void DrawPreeditRule(
        ICanvas c, string expanded, float origin, float y,
        ExpandedColumn from, ExpandedColumn to, float thickness, int z)
    {
        var left = origin + DiffText.CellsBefore(expanded, from.Value) * _monoAdvance;
        var right = origin + DiffText.CellsBefore(expanded, to.Value) * _monoAdvance;
        if (right <= left) return;

        c.DrawLine(new DrawLineInputs
        {
            Start = new PointF(left, y),
            End = new PointF(right, y),
            Thickness = thickness,
            Color = _styles.LineText,
            ZIndex = z,
        });
    }

    private void NoteCaretMoved()
    {
        if (!HasCaret) { _caretPhase = 0f; return; }
        if (_selection.Focus == _lastCaret) return;
        _lastCaret = _selection.Focus;
        _caretPhase = 0f;
    }

    private void UseCaretBlink(IFrameTicker ticker)
    {
        var tick = new Action<float>(AdvanceCaretBlink);
        this.Use(() =>
        {
            ticker.Add(tick);
            var subscriptions = new SubscriptionGroup();
            subscriptions.Add(() => ticker.Remove(tick));
            return subscriptions;
        });
    }

    private void AdvanceCaretBlink(float dt)
    {
        if (!HasCaret) return;
        var wasDrawn = CaretDrawn;
        _caretPhase = (_caretPhase + dt) % CaretBlinkSeconds;
        if (CaretDrawn != wasDrawn) SetDirty();
    }

    // ---- text selection ----

    // One file, so every position shares the single implicit scope: null.
    DiffSelectionModel IDiffSelectionSurface.Selection => _selection;

    RectF IDiffSelectionSurface.SelectionViewport => _list.Position;
    IReadOnlyList<DiffRow>? IDiffSelectionSurface.RowsOf(object? scope) => RowSource.Rows;
    Func<RowIndex, string?>? IDiffSelectionSurface.HiddenTextOf(object? scope) =>
        RowSource.HiddenText;
    void IDiffSelectionSurface.ScrollBy(float dy) => _list.SetScrollY(_list.ScrollY + dy);
    void IDiffSelectionSurface.RequestRedraw() => SetDirty();

    void IDiffSelectionSurface.FocusChanged(bool focused)
    {
        if (_focused == focused) return;
        _focused = focused;
        _caretPhase = 0f;
        _editorController.SyncIme();
        if (Document != null) SetDirty();
    }

    bool IDiffSelectionSurface.ShowSelectionMenu(PointF point)
    {
        if (!AssistantActions || _bus is null) return false;
        if (DescribeState(_renderState).Path is not { } path) return false;
        if (DiffSelectionQuote.Build(
                RowSource.Rows, _selection.Start, _selection.End, path, AnnotationsOf(_renderState)) is not { } quote)
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
        var hunkIndex = HunkIndexOf(HitTestListRow(point));
        return hunkIndex >= 0 && HitTestButton(point, hunkIndex) != HunkAction.None;
    }

    DiffTextHit? IDiffSelectionSurface.HitTestText(PointF point)
    {
        if (_lineHeight <= 0 || !_list.Position.ContainsPoint(point)) return null;
        var rowIndex = HitTestListRow(point);
        if (rowIndex < 0 || RowSource.Rows[rowIndex] is not DiffRow.Line line) return null;
        return new DiffTextHit(
            null, SnapToCaret(new DiffTextPos(new RowIndex(rowIndex), CharIndexAt(line.Text.Expanded, point.X))));
    }

    private DiffTextPos SnapToCaret(DiffTextPos pos) => Document?.Snap(pos) ?? pos;

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
        if (rowIndex < 0 || RowSource.Rows[rowIndex] is not DiffRow.Line line) return null;
        if (RowSource.NewLineAt(new RowIndex(rowIndex)) is not { } fileLine) return null;

        var column = line.Text.ToRaw(CharIndexAt(line.Text.Expanded, point.X), TabEdge.Before);
        return (line.Text, new FilePositionHit(fileLine, column));
    }

    DiffTextHit? IDiffSelectionSurface.ClampToScope(PointF point, object? scope)
    {
        if (_lineHeight <= 0 || RowSource.Rows.Count == 0) return null;
        var rowIndex = DragRowIndexAt(point);
        // A drag crossing a banner or a hunk bar keeps extending through it; those rows carry no
        // selectable text, so they contribute nothing to the copy.
        var text = RowSource.Rows[rowIndex] is DiffRow.Line line ? line.Text.Expanded : string.Empty;
        return new DiffTextHit(
            null, SnapToCaret(new DiffTextPos(new RowIndex(rowIndex), CharIndexAt(text, point.X))));
    }

    private ExpandedColumn CharIndexAt(string text, float x) =>
        _monoAdvance <= 0 ? default : new ExpandedColumn(DiffText.CharIndexAtCell(text, CellAt(x)));

    /// <summary>The monospace cell an x falls in, unclamped — negative over the gutters, past the
    /// line's own cell count out in the margin.</summary>
    private float CellAt(float x)
    {
        if (_monoAdvance <= 0) return 0f;
        var origin = DiffRowPainter.LineTextOriginX(
            _list.Position.Left - _scrollX, _gutterWidth, RowSource.SingleGutter, RowSource.FoldColumn,
            RowSource.GlyphColumn);
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
