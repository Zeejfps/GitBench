using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Assistant;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Components.VirtualRowList;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

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

    private static readonly TextStyle PlaceholderStyle = new()
    {
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };

    public event Action<float>? VerticalScrollPositionChanged
    {
        add => _list.VerticalScrollPositionChanged += value;
        remove => _list.VerticalScrollPositionChanged -= value;
    }

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
    /// <summary>The caret or the selection moved, by the keyboard, the pointer or a placement.</summary>
    public event Action? CaretMoved;

    public event Action<float>? HorizontalScrollPositionChanged
    {
        add => _scroll.HorizontalScrollPositionChanged += value;
        remove => _scroll.HorizontalScrollPositionChanged -= value;
    }

    public float VerticalScale => _list.VerticalScale;
    public float HorizontalScale => _scroll.HorizontalScale;

    private DiffContentStyles _styles = ThemeStyles.Dark.DiffContent;

    private DiffRenderState _renderState = new DiffRenderState.Placeholder("Select a file to view diff.");
    private DiffBody _body = new DiffBody.Viewer(DiffRowSet.Empty);

    private IDiffRowSource RowSource => _body.Rows;

    private Features.Editor.EditorBuffer? Document =>
        _body is DiffBody.Edited edited ? edited.Buffer : null;

    // The rows whose reshapes this view follows, and the selection named on the way into one.
    private Features.Editor.EditorRowSet? _followedRows;
    private Func<DiffTextPos, DiffTextPos?>? _reshapeRemap;
    private float _caretPhase;
    private bool _focused;
    private DiffTextPos _lastCaret;
    private DiffDiagnosticOverlay _diagnostics = DiffDiagnosticOverlay.Empty;
    private SemanticColorOverlay _semanticColors = SemanticColorOverlay.Empty;
    // Rows recolored under the current overlay, so a line is merged once rather than every frame.
    // Weak, because the rows are the document's: an edit replaces them and the old ones should go.
    private System.Runtime.CompilerServices.ConditionalWeakTable<DiffRow.Line, DiffRow.Line> _recolored = new();
    private DiffSearchOverlay _search = DiffSearchOverlay.Empty;
    // Whether the hits in hand were found in the file currently rendered. Resolved when either of
    // those changes rather than per row, and it is the whole of what gates both the wash and the
    // reveal: the two arrive from separate bindings, so on a file switch one of them is briefly the
    // other file's, and those line numbers would land on whatever now sits at them.
    private bool _searchApplies;
    private bool _usageLensRows;
    private FileSpan? _definitionLink;
    private readonly DiffRowPainter _painter;
    private readonly HunkButtonBar _buttonBar;
    private readonly DiffRowSurface _surface;
    private readonly DiffListScroll _scroll;

    public Action<int>? OnStageHunk { get => _surface.StageHunk; set => _surface.StageHunk = value; }
    public Action<int>? OnUnstageHunk { get => _surface.UnstageHunk; set => _surface.UnstageHunk = value; }
    public Action<int>? OnDiscardHunk { get => _surface.DiscardHunk; set => _surface.DiscardHunk = value; }
    public Action<int, GapExpandDirection>? OnExpandGap { get => _surface.ExpandGap; set => _surface.ExpandGap = value; }

    /// <summary>The same click held with <see cref="InputModifiers.Alt"/>: reveal the rest of the
    /// declaration rather than another fixed step of context.</summary>
    public Action<int, GapExpandDirection>? OnExpandGapToDeclaration
    {
        get => _surface.ExpandGapToDeclaration;
        set => _surface.ExpandGapToDeclaration = value;
    }

    private readonly VirtualRowListView _list;
    private readonly ILocalizationService _loc;
    private readonly Context _ctx;
    private readonly DiffSelectionModel _selection = new();
    private readonly DiffSelectionController _selectionController;
    private readonly Features.Editor.EditorController _editorController;
    private readonly IClipboard _clipboard;
    private readonly Features.Editor.DocumentSaves? _saves;
    private readonly Features.Editor.CompletionPopup? _completionPopup;
    private readonly IUiDispatcher? _dispatcher;

    // The list on screen and the word-start rect it was placed at, so a draw that finds the word
    // somewhere else can have it re-read.
    private Features.Editor.CompletionList? _completionList;
    private RectF? _completionAnchor;
    private bool _completionRefreshPosted;

    // Parameter info: the answer on screen, the caret row and column it was anchored at — held while
    // the caret stays on that row, so typing arguments does not drag it along — and where it was drawn.
    private readonly Features.Editor.SignaturePopup? _signaturePopup;
    private Lsp.SignatureHelp? _signatureHelp;
    private Features.Editor.TextPosition? _signatureOrigin;
    private RectF? _signatureAnchor;
    private bool _signatureRefreshPosted;

    /// <summary>Whether a selection here offers the assistant's quick actions. Only the main
    /// window's diff sets it: the assistant overlay is a child of that window, so an answer asked
    /// for from a pop-out would arrive somewhere the reader is not looking.</summary>
    public bool AssistantActions { get; set; }

    private FileLine? _pendingScrollLine;
    private (string Path, Features.Editor.TextPosition At)? _pendingCaret;

    // A guide's suggestion over the file, with the line it hangs from and, when it replaces lines,
    // the first of them, as edits have moved them. The buffer is the one it was drawn into.
    private Features.Editor.EditorHints? _hints;
    private FileLine? _ghostAnchor;
    private FileLine? _ghostFrom;
    private (string Path, Action<bool> Done)? _pendingTake;
    private ReplacedLine?[] _replaced = [];
    private Features.Editor.EditorBuffer? _ghostBuffer;
    private bool _ghostRefreshPosted;
    private FileSpan? _pendingSearchReveal;
    private FileLine? _lastTopLine;
    private bool _topLinePublished;
    private FoldState? _foldState;

    public DiffContentView(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        var theme = ctx.Theme();
        _ctx = ctx;
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
        _scroll = new DiffListScroll(_list, ContentWidth, () => Position.Width);
        _list.HorizontalWheelHandler = deltaX =>
        {
            if (_scroll.ScrollXBy(-deltaX * _list.ScrollWheelStep)) SetDirty();
        };
        _surface = new DiffRowSurface(_list, _painter, _buttonBar, _scroll, SetDirty)
        {
            Selection = _selection,
            ToggleFold = id => OnToggleFold?.Invoke(id),
            ActivateLens = (target, anchor) => UsageLensActivated?.Invoke(target, anchor),
        };

        AddChildToSelf(_list);
        _list.UseController(input, () => new VirtualRowListController(_list));
        this.UseController(input, () => new DiffMouseController(_surface), EventPhaseFilter.Capture);
        _clipboard = ctx.Require<IClipboard>();
        _saves = Features.Editor.DocumentSaves.From(ctx);
        _dispatcher = ctx.Get<IUiDispatcher>();
        var servers = _dispatcher is null ? null : ctx.Get<Features.LanguageServers.ILanguageServerStore>();
        var completionFeed = servers is null ? null : new Features.Editor.CompletionFeed(servers, _dispatcher!);
        var parameterHints = servers is null
            ? null
            : new Features.Editor.ParameterHints(servers, _dispatcher!, PresentSignatures, ArgumentAt);
        _editorController = new Features.Editor.EditorController(
            this, input, ctx.KeyMap(), completionFeed, parameterHints);
        if (ctx.Get<IPopupWindowFactory>() is { } popups && ctx.Get<IWindowCoordinates>() is { } coordinates)
        {
            _completionPopup = new Features.Editor.CompletionPopup(popups, coordinates);
            _signaturePopup = new Features.Editor.SignaturePopup(popups, coordinates);
        }
        var editorFontSize = ctx.Get<IWritable<EditorFontSize>>();
        _selectionController = new DiffSelectionController(this, input, _clipboard, _editorController)
        {
            Zoom = editorFontSize is null ? null : new Features.Editor.EditorZoomKeys(ctx.KeyMap(), editorFontSize),
        };
        this.UseController(input, _selectionController, EventPhaseFilter.Both);
        _selection.Changed += () => CaretMoved?.Invoke();
        // A view torn down with a suggestion up lets go of the buffer it drew it into.
        this.Use(() => new ActionDisposable(() => SetHints(null)));

        if (ctx.Get<IFrameTicker>() is { } ticker) UseCaretBlink(ticker);

        this.BindThemed(theme, s =>
        {
            _styles = s.DiffContent;
            _surface.ButtonStyles = s.DiffHunkButton;
            _painter.Styles = s.DiffContent;
            SetDirty();
        });

        // Placeholder/conflict text is custom-painted, so repaint on a live language switch.
        // Hunk-button labels are measured and cached; drop the cache so they re-measure in the
        // new language on the next draw.
        this.Bind(_loc.Strings, _ => { _buttonBar.InvalidateMetrics(); SetDirty(); });

        if (editorFontSize != null)
            this.Bind(editorFontSize, size => { _painter.CodeFontSize = size.Points; SetDirty(); });
    }

    // The VM's per-hunk index states for the WorkingTree view (see
    // DiffViewModel.WorkingTreeHunkStates); aligned with the current render's hunk list.
    public void SetWorkingTreeHunkStates(IReadOnlyList<WorkingTreeHunkState>? states)
    {
        _surface.HunkStates = states;
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
        var prevScrollX = _scroll.X;
        var remap = SelectionRemap();

        _renderState = state;
        _surface.ClearHover();
        _surface.HunkButtons = false;
        _surface.Side = DiffSide.Unstaged;

        _body = document is null
            ? new DiffBody.Viewer(DiffRowSet.Build(state, _loc, FoldsFor(state), _usageLensRows))
            : Opened(document, state);
        Follow(document?.Rows);
        _surface.Rows = RowSource;
        if (state is DiffRenderState.Loaded loaded)
        {
            _surface.Side = loaded.Result.Side;
            _surface.HunkButtons = HunkPatchBuilder.CanPatchHunk(loaded.Result)
                && HunkButtonBar.ActionsFor(loaded.Result.Side).Length > 0;
        }

        var (newPath, _) = DescribeState(state);
        if (newPath != prevPath)
        {
            _editorController.CloseCompletions();
            _editorController.CloseParameterInfo();
        }
        if (newPath != prevPath) _selection.Clear();
        else _selection.Remap(remap);
        if (newPath != prevPath) _pendingScrollLine = null;
        // A different file republishes its top line even when the number is unchanged: it is a
        // different declaration at line 1.
        if (newPath != prevPath) _topLinePublished = false;

        RefreshSearchScope();
        ApplyGhost();
        _list.ItemCount = RowSource.Rows.Count;
        _list.NotifyItemsChanged();
        ApplyScrollForTransition(state, prevPath, prevWasFullFile, prevTopLine, prevScrollY, prevScrollX);
        _editorController.SyncIme();
        SetDirty();
    }

    private DiffBody Opened(Features.Editor.EditorBuffer document, DiffRenderState state)
    {
        var annotations = AnnotationsOf(state);
        document.Rows.FoldExpanded = path => OnToggleFold?.Invoke(path);
        // Through ApplyRead, not Rows.SetAnnotations: this render state was built from a read of the file on
        // disk, so it describes whatever revision that read found — which is the revision the
        // buffer was opened at only until someone types. Stamping it with the opening revision
        // regardless refuses a re-read that is in fact current, and would overwrite a parse of the
        // buffer with a parse of the file.
        document.ApplyRead(annotations ?? new DiffAnnotations(null, null, null));
        document.Rows.SetFolds(FoldsFor(state));
        document.Rows.UsageLensRows = _usageLensRows;
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
        if (Document is { } document) document.Rows.SetFolds(FoldsFor(_renderState));
        else _body = new DiffBody.Viewer(
            DiffRowSet.Build(_renderState, _loc, FoldsFor(_renderState), _usageLensRows));
        _surface.Rows = RowSource;
        _selection.Remap(remap);
        _surface.ClearHover();
        _list.ItemCount = RowSource.Rows.Count;
        _list.NotifyItemsChanged();
        if (topLine is { } line) ScrollToNewLine(line, leadIn: 0);
        _editorController.SyncIme();
        SetDirty();
    }

    /// <summary>Follows the reshapes of the rows on screen, and stops following the ones that
    /// left.</summary>
    private void Follow(Features.Editor.EditorRowSet? rows)
    {
        if (ReferenceEquals(_followedRows, rows)) return;
        if (_followedRows is { } previous)
        {
            previous.Reshaping -= OnRowsReshaping;
            previous.Reshaped -= OnRowsReshaped;
        }

        _followedRows = rows;
        _reshapeRemap = null;
        if (rows is null) return;
        rows.Reshaping += OnRowsReshaping;
        rows.Reshaped += OnRowsReshaped;
    }

    private void OnRowsReshaping() => _reshapeRemap = SelectionRemap();

    private void OnRowsReshaped()
    {
        if (_reshapeRemap is { } remap) _selection.Remap(remap);
        _reshapeRemap = null;
        _surface.ClearHover();
        ReconcileRows();
        _editorController.SyncIme();
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
        _scroll.RestoreX(sameFile ? prevScrollX : 0f);

        if (sameFile)
        {
            // Same file. A flipped mode is a toggle → remap the top line into the new layout;
            // an unchanged mode is a re-emit (highlight attach, working-tree reload) → keep the
            // exact offset so neither the highlight nor a toggle's follow-up snaps to the top.
            if (newIsFullFile != prevWasFullFile && prevTopLine is { } top)
                ScrollToNewLine(top, ScrollLeadIn);
            else
                _scroll.SetTarget(prevScrollY);
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

        _scroll.SetTarget(0f);
    }

    private static (string? Path, bool IsFullFile) DescribeState(DiffRenderState state) => state switch
    {
        DiffRenderState.Loaded l => (l.Result.Path, false),
        DiffRenderState.FullFile ff => (ff.Path, true),
        DiffRenderState.Binary b => (b.Path, false),
        _ => (null, false),
    };

    public void SetVerticalNormalizedScrollPosition(float normalized) =>
        _list.SetVerticalNormalizedScrollPosition(normalized);

    public void SetHorizontalNormalizedScrollPosition(float normalized)
    {
        _scroll.SetNormalizedX(normalized);
        SetDirty();
    }

    // The new-file line of the topmost visible row, used to preserve the reading position across a
    // Diff↔FullFile toggle. Skips banners/separators and removed rows (no new-side number). Null
    // before metrics resolve or when no row from there down stands for a new-side line.
    public FileLine? TopVisibleNewLine()
    {
        var count = RowSource.Rows.Count;
        if (_surface.LineHeight <= 0 || count == 0) return null;
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
    /// screen — the find bar's hits, and diagnostics that do not say what text they are about.</summary>
    private bool ReadStillDescribesTheDocument => Document is not { ReadIsCurrent: false };

    /// <summary>
    /// Brings a hit into view on both axes, and only as far as it has to: stepping through hits
    /// that are already on screen must leave the text where the reader is reading it.
    /// </summary>
    public void RevealSearchMatch(FileSpan match)
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
        if (_surface.LineHeight <= 0) return;
        _pendingSearchReveal = null;

        if (RowSource.RowNearestNewLine(match.Line) is not { } row) return;
        if (RowSource.Rows[row.Value] is not DiffRow.Line line) return;
        if (!_list.TryGetRowRect(row.Value, out var rowRect)) return;
        EnsureVisible(SpanRect(line.Text, match, rowRect));
    }

    private RectF SpanRect(DiffLineText text, FileSpan match, RectF rowRect)
    {
        var origin = _surface.TextOriginX();
        var advance = _surface.MonoAdvance;
        var left = origin + DiffText.CellsBefore(text.Expanded, text.ToExpanded(match.Start).Value) * advance;
        var right = origin + DiffText.CellsBefore(text.Expanded, text.ToExpanded(match.End).Value) * advance;
        return new RectF(left, rowRect.Bottom, Math.Max(0f, right - left), rowRect.Height);
    }

    private const int RevealMarginCells = 4;

    public void RequestScrollToNewLine(FileLine line)
    {
        _pendingScrollLine = line;
        ApplyPendingScrollLine();
        SetDirty();
    }

    /// <summary>Puts the caret at a place in the edited file, takes the keyboard unless a field has
    /// it, and scrolls the
    /// line a third of the way down the viewport. Held until <paramref name="path"/> is the document
    /// on screen and its rows are measured, so it can be asked for before the file has loaded.</summary>
    public void RequestCaretAt(string path, Features.Editor.TextPosition at)
    {
        _pendingCaret = (path, at);
        ApplyPendingCaret();
        SetDirty();
    }

    private void ApplyPendingCaret()
    {
        if (_pendingCaret is not { } pending || _surface.LineHeight <= 0) return;
        if (Document is not { } editor || !PathKey.Comparer.Equals(PathKey.Normalize(editor.Path), PathKey.Normalize(pending.Path))) return;
        _pendingCaret = null;

        var at = editor.Document.Clamp(pending.At);
        editor.Write(_selection, Features.Editor.SelectionRange.At(at), null);
        ReconcileRows();
        if (RowSource.RowNearestNewLine(at.Line) is { } row)
            _scroll.SetTarget(ContentOffsetOf(row.Value) - _list.Position.Height / 3f);
        _selectionController.TakeFocusUnlessTyping();
        NoteCaretMoved();
    }

    /// <summary>Lays a guide's suggestion over the file, or takes it away: lines drawn after a line
    /// — shrinking as the reader types them — or in place of lines, which are lit up. Only over the
    /// file it names; another file on screen shows none.</summary>
    public void SetHints(Features.Editor.EditorHints? hints)
    {
        _hints = hints;
        _surface.AcceptSuggestion = hints?.Accept;
        (_ghostFrom, _ghostAnchor) = hints?.Ghost.Place switch
        {
            null => (null, null),
            Features.Editor.GhostPlace.Insert insert => ((FileLine?)null, (FileLine?)insert.After),
            Features.Editor.GhostPlace.Replace replace => (replace.From, replace.To),
            _ => throw new InvalidOperationException("Unknown ghost place."),
        };
        ApplyGhost();
        SetDirty();
    }

    /// <summary>Types the suggestion into the file as <see cref="TakeGhost"/> does, once
    /// <paramref name="path"/> is the document on screen, so it can be asked for while the file is
    /// still loading. A newer request answers the one before it false.</summary>
    public void RequestTakeGhost(string path, Action<bool> done)
    {
        _pendingTake?.Done(false);
        _pendingTake = null;
        if (Document is { } editor && IsHinted(editor.Path, path))
        {
            done(TakeGhost(path));
            return;
        }

        _pendingTake = (path, done);
    }

    private void RunPendingTake()
    {
        if (_pendingTake is not { } pending || Document is not { } editor || !IsHinted(editor.Path, pending.Path)) return;
        _pendingTake = null;
        pending.Done(TakeGhost(pending.Path));
    }

    /// <summary>Types the suggestion into the file as one undo step: what is left of it after the
    /// line it hangs from, or all of it in place of the lines it replaces. Answers whether there was
    /// anything to take.</summary>
    public bool TakeGhost(string path)
    {
        if (Document is not { } editor || !IsHinted(editor.Path, path)) return false;
        if (_hints?.Ghost is not { } ghost || _ghostAnchor is not { } anchor) return false;

        Features.Editor.SelectionRange range;
        string text;
        if (_ghostFrom is { } from)
        {
            var start = editor.Document.Clamp(Features.Editor.TextPosition.At(from.Value, 0));
            var last = new FileLine(Math.Max(from.Value, anchor.Value));
            var end = editor.Document.Clamp(new Features.Editor.TextPosition(last, new RawColumn(editor.Document.Line(last).Length)));
            range = new Features.Editor.SelectionRange(start, end);
            text = string.Join("\n", ghost.Lines);
        }
        else
        {
            if (editor.Rows.Ghost is not { Lines.Count: > 0 } left) return false;
            var line = editor.Document.Line(left.After);
            range = Features.Editor.SelectionRange.At(new Features.Editor.TextPosition(left.After, new RawColumn(line.Length)));
            text = "\n" + string.Join("\n", left.Lines);
        }

        var pasted = editor.Session.Paste(range, text);
        editor.Write(_selection, pasted, null);
        ReconcileRows();
        ((Features.Editor.IEditorSurface)this).RevealCaret();
        return true;
    }

    private static bool IsHinted(string documentPath, string hintedPath) =>
        PathKey.Comparer.Equals(PathKey.Normalize(documentPath), PathKey.Normalize(hintedPath));

    private ReplacedLine? ReplacedAt(int rowIndex)
    {
        if (_ghostFrom is not { } from || _hints is not { } hints) return null;
        if (Document is not { } editor || !IsHinted(editor.Path, hints.Path)) return null;
        if (RowSource.NewLineAt(new RowIndex(rowIndex)) is not { } line) return null;
        var index = line.Value - from.Value;
        return index >= 0 && index < _replaced.Length ? _replaced[index] : null;
    }

    // The suggestion under the lines it replaces, paired with them line by line as a diff's replace
    // block is, so each pair shows the characters that change.
    private Features.Editor.GhostLines Replacement(Features.Editor.TextDocument document, FileLine from, FileLine to, IReadOnlyList<string> lines)
    {
        var count = Math.Clamp(to.Value - from.Value + 1, 0, Math.Max(0, document.LineCount - from.Value + 1));
        var replaced = new ReplacedLine?[count];
        var added = new IReadOnlyList<CharRange>?[lines.Count];
        for (var k = 0; k < count; k++)
        {
            IReadOnlyList<CharRange>? emphasis = null;
            if (k < lines.Count)
            {
                var (old, @new) = IntraLineDiff.ForPair(
                    DiffText.ExpandTabs(document.Line(new FileLine(from.Value + k))), DiffText.ExpandTabs(lines[k]));
                if (old.Count > 0) emphasis = old;
                if (@new.Count > 0) added[k] = @new;
            }

            // A blank line going is not worth a red band: it is how an empty file reads.
            replaced[k] = document.Line(new FileLine(from.Value + k)).Trim().Length == 0 ? null : new ReplacedLine(emphasis);
        }

        _replaced = replaced;
        return new Features.Editor.GhostLines(to, lines, added);
    }

    // Draws the suggestion into the buffer on screen when it is the hinted file, and takes it out of
    // whichever buffer had it before.
    private void ApplyGhost()
    {
        var target = Document is { } editor && _hints is { } hints && IsHinted(editor.Path, hints.Path)
            ? editor
            : null;
        if (!ReferenceEquals(_ghostBuffer, target))
        {
            if (_ghostBuffer is { } previous)
            {
                previous.Edited -= OnGhostedEdit;
                previous.Rows.SetGhost(null);
            }

            _ghostBuffer = target;
            if (target is not null) target.Edited += OnGhostedEdit;
        }

        if (target is null || _hints?.Ghost is not { } ghost || _ghostAnchor is not { } anchor) return;
        var document = target.Document;
        target.Rows.SetGhost(_ghostFrom is { } from
            ? Replacement(document, from, anchor, ghost.Lines)
            : Features.Editor.GhostMatch.Remaining(ghost.Lines, anchor, document.LineCount, n => document.Line(new FileLine(n))));
        ReconcileRows();
        if (_pendingTake is not null) _dispatcher?.Post(RunPendingTake);
    }

    // Mid-edit the rows are the editor's; the suggestion is re-matched once the keystroke is done.
    private void OnGhostedEdit(Features.Editor.DocumentEdit edit)
    {
        if (_ghostAnchor is { } anchor) _ghostAnchor = Features.Editor.GhostMatch.Shift(anchor, edit.Inverse);
        if (_ghostFrom is { } from) _ghostFrom = Features.Editor.GhostMatch.Shift(from, edit.Inverse);
        if (_ghostRefreshPosted || _dispatcher is null) return;
        _ghostRefreshPosted = true;
        _dispatcher.Post(() =>
        {
            _ghostRefreshPosted = false;
            ApplyGhost();
        });
    }

    /// <summary>Where the caret is in the edited file, or null when nothing is being edited or no
    /// caret has been placed.</summary>
    public Features.Editor.TextPosition? Caret => CaretInDocument();

    /// <summary>How many lines of a selection <see cref="SelectedText"/> reads: it is published on
    /// every caret move, and a select-all in a large file would otherwise be copied per keystroke.</summary>
    private const int SelectedTextLines = 200;

    /// <summary>The text the edited file's selection covers, up to its first
    /// <see cref="SelectedTextLines"/> lines; empty for a bare caret.</summary>
    public string SelectedText
    {
        get
        {
            if (Document is not { } editor || !_selection.IsActive) return string.Empty;
            var range = editor.Document.Clamp(editor.SelectionOf(_selection).Range);
            if (range.Start == range.End) return string.Empty;
            if (range.End.Line.Value - range.Start.Line.Value >= SelectedTextLines)
                range = new Features.Editor.TextRange(range.Start, Features.Editor.TextPosition.At(range.Start.Line.Value + SelectedTextLines, 0));
            return editor.Document.Slice(range);
        }
    }

    private void ApplyPendingScrollLine()
    {
        if (_pendingScrollLine is not { } line || _surface.LineHeight <= 0) return;
        _pendingScrollLine = null;
        ScrollToNewLine(line, ScrollLeadIn);
    }

    // Scrolls so the row for the given new-file line sits leadIn rows below the top. No-op when no
    // row stands for it or for anything above it.
    public void ScrollToNewLine(FileLine line, int leadIn)
    {
        if (_surface.LineHeight <= 0) return;
        if (RowSource.RowNearestNewLine(line) is not { } row) return;
        _scroll.SetTarget(ContentOffsetOf(Math.Max(0, row.Value - leadIn)));
    }

    // A row's distance from the top of the content. Rows are not all one height, so this comes off
    // the widget's own offset table rather than a product: it places a row's top at
    // Position.Top + ScrollY − offset, and the offset is what that leaves behind.
    private float ContentOffsetOf(int rowIndex) =>
        _list.TryGetRowRect(rowIndex, out var rect) ? _list.Position.Top + _list.ScrollY - rect.Top : 0f;

    private float RowHeightAt(int rowIndex)
    {
        var height = _surface.LineHeight > 0 ? _surface.LineHeight : AssumedFontSize;
        var rows = RowSource.Rows;
        return rowIndex >= 0 && rowIndex < rows.Count ? DiffRowMetrics.HeightOf(rows[rowIndex], height) : height;
    }

    // Always at least the viewport: short diffs shouldn't leave dead space on the right where the
    // colored row backgrounds would visibly stop short of the edge.
    private float ContentWidth() => Math.Max(Position.Width, _surface.NaturalWidth());

    private void EnsureMetrics(ICanvas c)
    {
        _buttonBar.EnsureMetrics(c);
        DiffRowSurface.ResolveMetrics(_painter, c);

        // Resolved row height feeds the widget's offset table; it'll re-clamp its scroll on next
        // draw. Heights are per-row, so the table has to be discarded, not just the base height.
        var lineHeight = _surface.LineHeight;
        if (lineHeight > 0 && Math.Abs(_list.RowHeight - lineHeight) > 0.0001f)
        {
            _list.RowHeight = lineHeight;
            _list.InvalidateRowHeights();
        }
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        var pos = Position;
        var z = GetDrawZIndex();
        TrackCompletionAnchor();
        TrackSignatureAnchor();

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
                _scroll.PublishX(viewportFits: true);
                return;
            case DiffRenderState.Conflict:
                // The embedded pane swaps in the rich resolution view; this fallback is only
                // hit by the pop-out window, which has no resolution UI.
                DrawPlaceholder(c, pos, _loc.Strings.Value.DiffResolveInMain, _styles.PlaceholderText, z + 1);
                _scroll.PublishX(viewportFits: true);
                return;
            case DiffRenderState.Binary:
                DrawPlaceholder(c, pos, _loc.Strings.Value.DiffBinaryNotShown, _styles.PlaceholderText, z + 1);
                _scroll.PublishX(viewportFits: true);
                return;
            case DiffRenderState.Loaded when RowSource.Rows.Count == 0:
                DrawPlaceholder(c, pos, _loc.Strings.Value.DiffNoChanges, _styles.PlaceholderText, z + 1);
                _scroll.PublishX(viewportFits: true);
                return;
        }

        EnsureMetrics(c);
        _scroll.ClampX();
        ApplyPendingScrollLine();
        ApplyPendingCaret();
        ApplyPendingSearchReveal();
        _scroll.ReassertTarget();
        NotifyTopVisibleLine();
        _selectionController.Tick();
        NoteCaretMoved();
        // After the geometry above and before the child list draws its rows.
        _editorController.SyncIme();
        _scroll.PublishX();
    }

    private void NotifyTopVisibleLine()
    {
        var line = TopVisibleNewLine();
        if (_topLinePublished && line == _lastTopLine) return;
        _lastTopLine = line;
        _topLinePublished = true;
        TopVisibleLineChanged?.Invoke(line);
    }

    private void DrawDiffRowAt(ICanvas c, RectF rowRect, int rowIndex, RowRenderState state, int z)
    {
        var rows = RowSource.Rows;
        if (rowIndex < 0 || rowIndex >= rows.Count) return;

        var paint = _surface.PaintFor(rowRect, rowIndex, z);
        var composing = ComposedOn(rowIndex);
        if (composing is { } shift)
            paint = paint with
            {
                Selection = _selection.TryRowSpan(null, new RowIndex(rowIndex), shift.Line.Text.End, out var span)
                    ? Shifted(span, shift)
                    : null,
            };
        else
            paint = paint with
            {
                Diagnostics = MarksOnRow(rowIndex),
                Link = LinkOnRow(rowIndex),
                Search = SearchOnRow(rowIndex),
                Replaced = ReplacedAt(rowIndex),
            };
        _surface.DrawRow(c, rowRect, rowIndex, z, composing?.Line ?? Recolored(rows[rowIndex]), paint);

        if (composing is { } preedit) DrawPreeditUnderlines(c, preedit, rowRect, z + 3);

        if (CaretRectOn(rowIndex, rowRect) is { } caret)
            c.DrawRect(new DrawRectInputs
            {
                Position = caret,
                Style = new RectStyle { BackgroundColor = _styles.Caret },
                ZIndex = z + 7,
            });
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

    public IReadOnlyList<Lsp.Diagnostic> DiagnosticsOn(FileLine line) =>
        DiagnosticsApplyTo(line) ? _diagnostics.On(line) : [];

    /// <summary>
    /// Whether the diagnostics on a line are still about what it says. Where the overlay knows the
    /// text the server was shown, that is a question about this one line, so typing elsewhere leaves
    /// a line's marks alone; where it does not, only a document nobody has typed into qualifies.
    /// </summary>
    private bool DiagnosticsApplyTo(FileLine line)
    {
        if (!_diagnostics.KnowsItsText) return ReadStillDescribesTheDocument;
        if (Document is not { } editor) return true;
        return line.Value <= editor.Document.LineCount
            && _diagnostics.StillDescribes(line, editor.Document.Line(line));
    }

    /// <summary>
    /// Replaces what a language server says the file's types are. Not through
    /// <see cref="SetRenderState"/>, for the reason <see cref="SetDiagnostics"/> is not: it arrives
    /// after the file, again after each wave of analysis, and changes only colors.
    /// </summary>
    public void SetSemanticColors(SemanticColorOverlay colors)
    {
        if (ReferenceEquals(_semanticColors, colors)) return;
        _semanticColors = colors;
        _recolored = new();
        SetDirty();
    }

    private DiffRow Recolored(DiffRow row)
    {
        if (row is not DiffRow.Line line || _semanticColors.IsEmpty) return row;
        if (_renderState is not DiffRenderState.FullFile file || file.Path != _semanticColors.Path) return row;

        return _recolored.GetValue(line, l => l with { Spans = _semanticColors.Recolor(l.Text, l.Spans) });
    }

    /// <summary>
    /// Replaces what is known about the file's usages. Deliberately not through
    /// <see cref="SetRenderState"/>, for the same reason <see cref="SetDiagnostics"/> is not:
    /// counts trickle in for as long as a file is open, and re-flattening on each would shuffle
    /// the file under the reader dozens of times while they were trying to read it.
    /// </summary>
    public void SetUsageLens(UsageLensOverlay usages)
    {
        if (ReferenceEquals(_surface.UsageLens, usages)) return;
        _surface.UsageLens = usages;
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
            document.Rows.UsageLensRows = value;
            _selection.Remap(remap);
            _surface.ClearHover();
            ReconcileRows();
        }
    }

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
            if (rows[i] is DiffRow.Lens lens) targets.Add(DiffRowSurface.TargetOf(lens));
        return targets;
    }

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
        if (_diagnostics.IsEmpty) return null;
        if (RowSource.Rows[rowIndex] is not DiffRow.Line line) return null;
        if (RowSource.NewLineAt(new RowIndex(rowIndex)) is not { } fileLine) return null;
        if (!DiagnosticsApplyTo(fileLine)) return null;

        var marks = _diagnostics.MarksOn(fileLine, line.Text);
        return marks.Count == 0 ? null : marks;
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

    public bool TryClickExpander(PointF point, InputModifiers modifiers = InputModifiers.None) =>
        _surface.ClickExpander(point, modifiers);

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
        Document != null && _focused && _selection.IsActive && _surface.MonoAdvance > 0;

    private bool CaretDrawn => HasCaret && _caretPhase < CaretBlinkSeconds / 2f;

    Features.Editor.EditorBuffer? Features.Editor.IEditorSurface.Editor => Document;
    DiffSelectionModel Features.Editor.IEditorSurface.Selection => _selection;
    object? Features.Editor.IEditorSurface.SelectionScope => null;
    void Features.Editor.IEditorSurface.RequestRedraw() => SetDirty();
    bool Features.Editor.IEditorSurface.CopySelection() => _selectionController.Copy();
    bool Features.Editor.IEditorSurface.SelectAllText() => _selectionController.SelectAll();
    string? Features.Editor.IEditorSurface.ClipboardText() => _clipboard.GetText();

    void Features.Editor.IEditorSurface.RequestSave()
    {
        if (Document is { } editor) _saves?.Save(editor.Path, editor.Document, editor.Encoding);
    }

    void Features.Editor.IEditorSurface.RowsChanged() => ReconcileRows();

    void Features.Editor.IEditorSurface.PresentCompletions(Features.Editor.CompletionList? list)
    {
        _completionList = list;
        _completionAnchor = list is null ? null : WordStartRect(list);
        if (_completionPopup is null) return;
        // A list still waiting on its server is tracked but not drawn: an empty popup is a thing
        // to look at that says nothing.
        if (_completionAnchor is { } anchor && list is { Items.Count: > 0 }) _completionPopup.Show(list, anchor);
        else _completionPopup.Hide();
    }

    void Features.Editor.IEditorSurface.PresentCompletionDocs(string? markdown) =>
        _completionPopup?.ShowDocs(markdown);

    /// <summary>Where a caret standing at the start of the word being completed would be drawn.
    /// Identifiers are one cell a character, so this is the caret's rect stepped back over what
    /// has been typed of it.</summary>
    private RectF? WordStartRect(Features.Editor.CompletionList list) =>
        CaretRect() is { } caret
            ? caret with { Left = caret.Left - list.Prefix.Length * _surface.MonoAdvance }
            : null;

    /// <summary>Shows parameter info, or hides it for null. Anchored where the caret was when it
    /// opened for as long as the caret stays on that row.</summary>
    private void PresentSignatures(Lsp.SignatureHelp? help)
    {
        _signatureHelp = help;
        if (help is null || !_selection.IsActive)
        {
            _signatureOrigin = null;
            _signatureAnchor = null;
            _signaturePopup?.Hide();
            return;
        }

        var focus = CaretInDocument();
        if (_signatureOrigin is not { } origin || focus is not { } at || origin.Line != at.Line) _signatureOrigin = focus;
        _signatureAnchor = SignatureAnchorRect();
        if (_signaturePopup is null) return;
        if (_signatureAnchor is { } anchor) _signaturePopup.Show(help, anchor);
        else _signaturePopup.Hide();
    }

    private int? ArgumentAt(Features.Editor.TextPosition caret)
    {
        if (Document is not { } editor) return null;
        var options = editor.Session.Options;
        return Features.Editor.LineContext.ArgumentIndex(
            number => editor.Document.Line(new FileLine(number)),
            caret.Line.Value, caret.Column.Value, options.Typing, options.LineComment);
    }

    // Held as a document position rather than a row: a parse that adds or drops a usages row moves
    // every row index below it while the caret stays exactly where it was.
    private Features.Editor.TextPosition? CaretInDocument() =>
        Document is { } editor && _selection.IsActive ? editor.PositionOf(_selection.Focus) : null;

    private RectF? SignatureAnchorRect() =>
        _signatureOrigin is { } origin && CaretInDocument() is { } focus && origin.Line == focus.Line
            && CaretRect() is { } caret
            ? caret with { Left = caret.Left - (focus.Column.Value - origin.Column.Value) * _surface.MonoAdvance }
            : null;

    /// <summary>Has parameter info asked again once the caret has left the row it was anchored on —
    /// a click somewhere else — and redrawn where it belongs once a scroll has moved that row.</summary>
    private void TrackSignatureAnchor()
    {
        if (_signatureHelp is not { } help || _signatureRefreshPosted || _dispatcher is null) return;

        var anchor = SignatureAnchorRect();
        if (anchor == _signatureAnchor) return;

        _signatureRefreshPosted = true;
        _dispatcher.Post(() =>
        {
            _signatureRefreshPosted = false;
            if (!ReferenceEquals(_signatureHelp, help)) return;
            if (_signatureOrigin is { } origin && CaretInDocument() is { } focus && origin.Line == focus.Line)
                PresentSignatures(help);
            else _editorController.RefreshParameterInfo();
        });
    }

    /// <summary>Has an open list re-read when the word it completes has moved on screen — a scroll,
    /// a click — without a keystroke to say so. Posted, because a popup is a window and this runs
    /// inside a draw.</summary>
    private void TrackCompletionAnchor()
    {
        if (_completionList is not { } list || _completionRefreshPosted || _dispatcher is null) return;
        if (WordStartRect(list) == _completionAnchor) return;

        _completionRefreshPosted = true;
        _dispatcher.Post(() =>
        {
            _completionRefreshPosted = false;
            _editorController.RefreshCompletions();
        });
    }

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
        SetDirty();
    }

    int Features.Editor.IEditorSurface.PageRows =>
        _surface.LineHeight <= 0 ? 1 : Math.Max(1, (int)(_list.Position.Height / _surface.LineHeight) - 1);

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
        if (_surface.LineHeight > 0 && view.Height > 0)
        {
            if (rect.Top > view.Top) _scroll.SetTarget(_list.ScrollY + view.Top - rect.Top);
            else if (rect.Bottom < view.Bottom) _scroll.SetTarget(_list.ScrollY + view.Bottom - rect.Bottom);
        }

        var advance = _surface.MonoAdvance;
        if (advance <= 0 || view.Width <= 0) return;

        var margin = RevealMarginCells * advance;
        var delta = rect.Left - margin < view.Left ? rect.Left - view.Left - margin
            : rect.Right + margin > view.Right ? rect.Right - view.Right + margin
            : 0f;
        if (_scroll.ScrollXBy(delta)) SetDirty();
    }

    private RectF? CaretRectOn(int rowIndex, RectF rowRect) =>
        CaretDrawn && _selection.Focus.Row.Value == rowIndex
            ? CaretRectAt(rowIndex, rowRect)
            : null;

    private RectF? CaretRect()
    {
        if (!HasCaret) return null;
        var row = _selection.Focus.Row.Value;
        return _surface.TryGetRowRect(row, out var rowRect) ? CaretRectAt(row, rowRect) : null;
    }

    private RectF? CaretRectAt(int rowIndex, RectF rowRect)
    {
        if (rowIndex < 0 || rowIndex >= RowSource.Rows.Count) return null;
        var composing = ComposedOn(rowIndex);
        if ((composing?.Line ?? RowSource.Rows[rowIndex]) is not DiffRow.Line line) return null;

        var column = composing?.Caret ?? _selection.Focus.Char;
        var cells = DiffText.CellsBefore(line.Text.Expanded, column.Value);
        return new RectF(
            _surface.TextOriginX() + cells * _surface.MonoAdvance, rowRect.Bottom, CaretWidth, rowRect.Height);
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

    private void DrawPreeditUnderlines(ICanvas c, ComposedRow composing, RectF rowRect, int z)
    {
        var expanded = composing.Line.Text.Expanded;
        var origin = _surface.TextOriginX();
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
        var left = origin + DiffText.CellsBefore(expanded, from.Value) * _surface.MonoAdvance;
        var right = origin + DiffText.CellsBefore(expanded, to.Value) * _surface.MonoAdvance;
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
        if (!focused)
        {
            _editorController.CloseCompletions();
            _editorController.CloseParameterInfo();
        }
        _editorController.SyncIme();
        if (Document != null) SetDirty();
    }

    bool IDiffSelectionSurface.ShowSelectionMenu(PointF point)
    {
        if (!AssistantActions) return false;
        if (DescribeState(_renderState).Path is not { } path) return false;
        if (DiffSelectionQuote.Build(
                RowSource.Rows, _selection.Start, _selection.End, path, AnnotationsOf(_renderState)) is not { } quote)
            return false;

        var assistant = _ctx.Require<AssistantViewModel>();
        return RepoBarContextMenu.Show(_ctx, point, DiffAssistantMenu.Items(_loc.Strings.Value, quote, assistant.AskAboutSelection)) != null;
    }

    private static DiffAnnotations? AnnotationsOf(DiffRenderState state) => state switch
    {
        DiffRenderState.Loaded loaded => loaded.Annotations,
        DiffRenderState.FullFile fullFile => fullFile.Annotations,
        _ => null,
    };

    bool IDiffSelectionSurface.IsInteractiveAt(PointF point) => _surface.IsInteractiveAt(point);

    DiffTextHit? IDiffSelectionSurface.HitTestText(PointF point) =>
        _surface.TextPosAt(point) is { } pos ? new DiffTextHit(null, SnapToCaret(pos)) : null;

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
    public Features.Editor.TextPosition? HitTestFilePosition(PointF point) => FilePositionUnder(point)?.At;

    /// <summary>The identifier under a pixel, for the link a held modifier draws over one. Null
    /// wherever <see cref="HitTestFilePosition"/> is, and also over punctuation and whitespace.</summary>
    public FileSpan? HitTestIdentifier(PointF point)
    {
        if (FilePositionUnder(point) is not { } under) return null;
        // The glyph under the pointer, not the caret position nearest it: a link covers the word
        // it is drawn over and nothing either side of it, so the whitespace between two words has
        // to belong to neither.
        var expanded = DiffText.CharIndexOnCell(under.Text.Expanded, _surface.CellAt(point.X));
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

    private (DiffLineText Text, Features.Editor.TextPosition At)? FilePositionUnder(PointF point)
    {
        var rowIndex = _surface.RowAt(point);
        if (rowIndex < 0 || RowSource.Rows[rowIndex] is not DiffRow.Line line) return null;
        if (RowSource.NewLineAt(new RowIndex(rowIndex)) is not { } fileLine) return null;

        var column = line.Text.ToRaw(_surface.CharIndexAt(line.Text.Expanded, point.X), TabEdge.Before);
        return (line.Text, new Features.Editor.TextPosition(fileLine, column));
    }

    DiffTextHit? IDiffSelectionSurface.ClampToScope(PointF point, object? scope)
    {
        if (_surface.LineHeight <= 0 || RowSource.Rows.Count == 0) return null;
        var rowIndex = DragRowIndexAt(point);
        // A drag crossing a banner or a hunk bar keeps extending through it; those rows carry no
        // selectable text, so they contribute nothing to the copy.
        var text = RowSource.Rows[rowIndex] is DiffRow.Line line ? line.Text.Expanded : string.Empty;
        return new DiffTextHit(
            null, SnapToCaret(new DiffTextPos(new RowIndex(rowIndex), _surface.CharIndexAt(text, point.X))));
    }

    private MouseCursor CursorAt(PointF point) =>
        LinkCovers(point) ? MouseCursor.Hand : _surface.CursorAt(point);

    // The link was computed for one pixel and then held, so that pixel can stop being over it —
    // a scroll slides the line out from under a cursor that never moved. The cursor shape asks
    // where the pointer is now rather than trusting the mark.
    private bool LinkCovers(PointF point) =>
        _definitionLink is { } link &&
        HitTestFilePosition(point) is { } at &&
        at.Line == link.Line &&
        at.Column.Value >= link.Start.Value &&
        at.Column.Value <= link.End.Value;
}
