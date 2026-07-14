using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.VirtualRowList;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Review;

/// <summary>
/// The review window's right column: every file of the combined range stacked in one scrolling
/// surface, GitHub-PR style, with external scrollbars synced to the list.
/// </summary>
internal sealed record ReviewDiffPanel : IWidget
{
    public View BuildView(Context ctx)
    {
        var content = new ReviewDiffListView(ctx);
        var vScrollBar = ScrollBars.CreateVertical(ctx);
        var hScrollBar = ScrollBars.CreateHorizontal(ctx);
        // The code grid it scrolls is pinned LTR (see DiffRowPainter), so the bar must not mirror:
        // normalized 0 stays the left edge in every locale.
        hScrollBar.IsRtl = false;
        content.Use(() => new ScrollSyncController(content, vScrollBar, hScrollBar));
        return new BorderLayoutView
        {
            Center = content,
            East = vScrollBar,
            South = hScrollBar,
        };
    }
}

/// <summary>
/// One scrolling, virtualized surface holding every file of the review range as an inset card —
/// a foldable header band (fold chevron, status, path, Viewed checkbox) over that file's diff
/// rows, with a breathing gap between files. Cost tracks the viewport, not the file count: rows
/// are drawn through one <see cref="VirtualRowListView"/> (variable heights), and a file's diff
/// is loaded only when its section nears the viewport — until then it holds a fixed-height
/// loading stub, and loads that finish above the viewport are scroll-anchored so the reading
/// position never jumps. The file being read keeps its header pinned to the viewport top,
/// GitHub-style, so the path and the Viewed toggle stay reachable mid-file. Marking a file
/// Viewed folds its section, and the tree's activation scrolls here. Scrolling does not change
/// the selection — only a click on a card, the tree, or j/k does.
/// </summary>
internal sealed class ReviewDiffListView : View, IScrollableContent, IDiffSelectionSurface
{
    private const float AssumedFontSize = FontSize.Body;
    private const float FallbackMonoAdvanceRatio = 0.6f;

    // Card geometry: each file draws as an inset card on the panel surface — side margins, a
    // 1px outline, and a gap band above every header so files never touch.
    private const float PanelPaddingX = 12f;
    private const float SectionGap = 12f;
    // Breathing room above the first card and below the last, as phantom padding rows in the
    // flattened surface. The top pad rides on the first header's own gap band, so both edges
    // read as the same visible padding. The sticky header pins this far below the viewport top
    // and the strip above it stays panel surface, so the padding survives stickiness.
    private const float PanelPaddingY = 24f;
    private const float TopPadRowHeight = PanelPaddingY - SectionGap;
    private const float HeaderBandHeight = 38f;
    private const float HeaderRowHeight = SectionGap + HeaderBandHeight;
    private const float MessageRowHeight = 44f;
    private const float HeaderPaddingX = 10f;
    private const float ChevronWidth = 16f;
    private const float StatusIconWidth = 18f;
    private const float ActiveBarWidth = 3f;
    // The clickable Viewed zone at the header's trailing edge (checkbox glyph + label).
    private const float ViewedZoneWidth = 96f;
    // The full-file toggle beside it (a lone glyph), shown once the card's diff is loaded.
    private const float FullFileZoneWidth = 24f;
    // Sections within this margin of the viewport get their diffs loaded ahead of arrival.
    private const float LoadMarginPx = 1600f;

    private static readonly TextStyle HeaderGlyphStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Body,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };
    private static readonly TextStyle HeaderPathStyle = new()
    {
        VerticalAlignment = TextAlignment.Center,
    };
    private static readonly TextStyle ViewedLabelStyle = new()
    {
        FontSize = FontSize.Caption,
        VerticalAlignment = TextAlignment.Center,
    };
    private static readonly TextStyle MessageStyle = new()
    {
        VerticalAlignment = TextAlignment.Center,
    };

    // One file's slice of the flattened surface: the header row (gap band + header) plus its body
    // rows — the built diff rows once loaded, a single message row (loading / binary / error /
    // empty) otherwise, nothing while folded.
    private sealed class Section
    {
        // Settable: a working-tree reconcile re-points a surviving section at the refreshed
        // FileChange (its status can shift as the file is staged or edited).
        public required FileChange File { get; set; }
        public CommitFileTab? Diff;
        public IDisposable? Subscription;
        public IDisposable? MarksSubscription;
        public DiffRenderState? Render;
        public DiffRowSet RowSet = DiffRowSet.Empty;
        public View? ConflictView;
        public bool Folded;
        public int StartRow;
        public int BodyRows;
        public float GutterWidth;

        public void DisposeDiff()
        {
            Subscription?.Dispose();
            MarksSubscription?.Dispose();
            Diff?.Dispose();
        }
    }

    private readonly Context _ctx;
    private readonly ILocalizationService _loc;
    private readonly IReviewSurfaceModel _vm;
    private readonly CommitDetailsViewModel _details;
    private readonly DiffRowPainter _painter;
    private readonly VirtualRowListView _list;
    // Selections are scoped to one file's card: the scope is its path, so a drag that runs onto
    // the neighbouring card stops at the card it started in.
    private readonly DiffSelectionModel _selection = new();
    private readonly DiffSelectionController _selectionController;

    private readonly List<Section> _sections = new();
    private readonly Dictionary<string, Section> _byPath = new(StringComparer.Ordinal);
    private HashSet<string> _viewedSnapshot = new(StringComparer.Ordinal);
    private readonly HunkButtonBar _buttonBar;
    private Section? _hoveredHunkSection;
    private int _hoveredHunkIndex = -1;
    private HunkAction _hoveredHunkButton = HunkAction.None;
    private int _heightCursor;
    // Identity of the file list on screen (the details surface's Sha: a commit, a base..head key, or
    // the working-tree sentinel). A new identity is new content — rebuild and reset the scroll; the
    // same identity means the same surface refreshed, so reconcile in place.
    private string? _contentKey;

    private ThemeStyles _theme = ThemeStyles.Dark;
    private float _lineHeight;
    private float _monoAdvance;
    private bool _metricsResolved;
    private float _naturalWidth;

    private float _scrollX;
    // A programmatic vertical scroll target re-asserted for a few frames — same guard as
    // DiffContentView: a scrollbar's hidden→visible transition can echo a stale position over a
    // just-set offset, so the target re-applies until it sticks or the budget runs out.
    private float? _pendingScrollY;
    private int _pendingScrollFrames;
    private float _lastNormalizedY;
    private float _lastNormalizedX;
    private float _lastVerticalScale = -1f;
    private float _lastHorizontalScale = -1f;

    public event Action<float>? VerticalScrollPositionChanged;
    public event Action<float>? HorizontalScrollPositionChanged;
    public float VerticalScale { get; private set; } = 1f;
    public float HorizontalScale { get; private set; } = 1f;

    public ReviewDiffListView(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        _ctx = ctx;
        _loc = ctx.Localization();
        _vm = ctx.Require<IReviewSurfaceModel>();
        _details = ctx.Require<CommitDetailsViewModel>();
        _painter = new DiffRowPainter(_loc);
        _buttonBar = new HunkButtonBar(_loc);

        _list = new VirtualRowListView
        {
            RowHeight = AssumedFontSize,
            RowHeightAt = RowHeightAt,
            ItemBuilder = DrawRowAt,
            ScrollWheelStep = Scrolling.WheelStep,
            CursorAt = CursorAt,
        };
        _list.ScrollChanged += OnScrolled;
        _list.HorizontalWheelHandler = OnHorizontalWheel;
        _list.RowClicked += OnRowClicked;
        _list.RowContextRequested += OnRowContextRequested;

        AddChildToSelf(_list);
        // Horizontally-scrolled diff rows are clipped only to the list bounds, so long lines would
        // bleed into the card margins; the overlay repaints the margin strips above row content,
        // and floats the sticky header over the rows.
        AddChildToSelf(new PanelOverlay(this));
        _list.UseController(input, () => new VirtualRowListController(_list));
        // Hover only — clicks flow through the list's RowClicked like the gap expanders. Attached
        // before the selection controller to mirror DiffContentView's ordering.
        this.UseController(input, () => new HunkHoverController(this), EventPhaseFilter.Capture);
        this.UseController(input, () => new PanelWheelController(this));
        _selectionController = new DiffSelectionController(this, input, ctx.Get<IClipboard>());
        this.UseController(input, _selectionController, EventPhaseFilter.Both);

        this.BindThemed(ctx.Theme(), s =>
        {
            _theme = s;
            _painter.Styles = s.DiffContent;
            SetDirty();
        });

        // The file list drives the sections. Loading keeps the current sections up
        // (stale-while-revalidate); a placeholder clears them.
        this.Bind(_details.RenderState, state =>
        {
            switch (state)
            {
                case CommitDetailsRenderState.Loaded l:
                    SetFiles(l.Details.Sha, l.Details.Files);
                    break;
                case CommitDetailsRenderState.Placeholder:
                    SetFiles(null, Array.Empty<FileChange>());
                    break;
            }
        });

        // Viewed marks fold their sections out of the way (and un-viewing brings the diff back),
        // whether the toggle came from a header checkbox, the 'v' key, or the primary action.
        this.Bind(_vm.ReviewedFiles.Revision, _ => SyncViewedFolds());

        // Repaint on active-file moves (the header accent) and language switches (message rows,
        // re-measured hunk-button labels).
        this.Bind(_vm.ActiveFile, _ => SetDirty());
        this.Bind(_loc.Strings, _ => { _buttonBar.InvalidateMetrics(); SetDirty(); });

        // Navigation (tree click, j/k, mark-viewed advance) scrolls the file's section here.
        this.Use(() =>
        {
            Action<string> handler = ScrollToFile;
            _vm.ScrollToFileRequested += handler;
            return new ActionDisposable(() => _vm.ScrollToFileRequested -= handler);
        });

        // Per-file diff view models are owned here; drop them with the view.
        this.Use(() => new ActionDisposable(ClearSections));
    }

    // ---- card geometry ----

    private float CardLeft() => _list.Position.Left + PanelPaddingX;
    private float CardRight() => _list.Position.Right - PanelPaddingX;
    private float CardViewportWidth() => Math.Max(0f, Position.Width - PanelPaddingX * 2);

    // ---- section structure ----

    // Adopts a file list. A different content key is different content — start over at the top. The
    // same key is the same surface refreshed (an editor save under the working-tree review, a range
    // reload that resolved to the same endpoints), so the sections reconcile in place: surviving
    // files keep their loaded diff, fold state and scroll offset, and the file being read stays put.
    private void SetFiles(string? contentKey, IReadOnlyList<FileChange> files)
    {
        if (contentKey == null || contentKey != _contentKey)
        {
            _contentKey = contentKey;
            RebuildSections(files);
            return;
        }
        ReconcileSections(files);
    }

    private void RebuildSections(IReadOnlyList<FileChange> files)
    {
        ClearSections();
        foreach (var f in files)
        {
            // Files already reviewed (marks survive a reload under the same range key) start folded.
            var section = new Section { File = f, Folded = _vm.IsFileViewed(f.Path) };
            _sections.Add(section);
            _byPath[f.Path] = section;
        }
        _viewedSnapshot = CurrentViewedSet();
        RebuildIndex();
        _list.SetScrollY(0f);
        SetDirty();
    }

    // Re-lays the sections over a refreshed file list without disturbing the reader: surviving
    // sections are moved, not recreated (their DiffViewModel reloads itself on the working-tree
    // change), vanished ones are disposed, and new ones arrive folded if already marked. The scroll
    // offset is corrected by however much content shifted above the file being read, so files
    // appearing or disappearing higher up don't slide it out from under the cursor.
    private void ReconcileSections(IReadOnlyList<FileChange> files)
    {
        var anchor = _vm.ActiveFile.Value is { } path && _byPath.TryGetValue(path, out var a) ? a : null;
        var anchorBefore = anchor == null ? 0f : SectionTopOffset(anchor);

        var next = new List<Section>(files.Count);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (_byPath.TryGetValue(f.Path, out var existing))
            {
                existing.File = f;
                next.Add(existing);
            }
            else
            {
                next.Add(new Section { File = f, Folded = _vm.IsFileViewed(f.Path) });
            }
            kept.Add(f.Path);
        }

        foreach (var s in _sections)
        {
            if (kept.Contains(s.File.Path)) continue;
            RemoveConflictView(s);
            s.DisposeDiff();
        }

        _sections.Clear();
        _sections.AddRange(next);
        _byPath.Clear();
        foreach (var s in _sections) _byPath[s.File.Path] = s;
        if (_selection.Scope is string selectedPath && !_byPath.ContainsKey(selectedPath))
            _selection.Clear();

        _viewedSnapshot = CurrentViewedSet();
        RecomputeNaturalWidth();
        var scroll = _list.ScrollY;
        RebuildIndex();
        if (anchor != null && _byPath.ContainsKey(anchor.File.Path))
            _list.SetScrollY(Math.Max(0f, scroll + (SectionTopOffset(anchor) - anchorBefore)));
        SetDirty();
    }

    private void ClearSections()
    {
        foreach (var s in _sections)
        {
            RemoveConflictView(s);
            s.DisposeDiff();
        }
        _sections.Clear();
        _byPath.Clear();
        _selection.Clear();
        SetHunkHover(null, -1, HunkAction.None);
        _naturalWidth = 0f;
        _heightCursor = 0;
    }

    // Reassigns the flattened row indices after any structural change (fold, load, rebuild).
    // Row 0 is the top padding row and the last row the bottom one; sections fill the space
    // between.
    private void RebuildIndex()
    {
        var row = 1;
        foreach (var s in _sections)
        {
            s.StartRow = row;
            s.BodyRows = s.Folded ? 0 : Math.Max(1, s.RowSet.Rows.Count);
            row += 1 + s.BodyRows;
        }
        _heightCursor = 0;
        _list.ItemCount = _sections.Count == 0 ? 0 : row + 1;
        _list.NotifyItemsChanged();
    }

    // The section containing a flattened row (binary search over the start indices), with the
    // row's index within it: 0 = the header, 1..BodyRows = body rows. The padding rows at either
    // end belong to no section.
    private Section? Locate(int row, out int local)
    {
        local = 0;
        if (_sections.Count == 0 || row < 0) return null;
        int lo = 0, hi = _sections.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_sections[mid].StartRow <= row) lo = mid;
            else hi = mid - 1;
        }
        var s = _sections[lo];
        local = row - s.StartRow;
        return local >= 0 && local <= s.BodyRows ? s : null;
    }

    // Row height for the virtual list. The list's offset table rebuild queries indices in
    // ascending order, so a riding cursor answers amortized O(1) instead of a binary search per
    // row; random probes (draw, hit-test) stay near the cursor.
    private float RowHeightAt(int i)
    {
        if (_sections.Count == 0) return LineHeight();
        if (i == 0) return TopPadRowHeight;
        if (i == _list.ItemCount - 1) return PanelPaddingY;
        if (_heightCursor >= _sections.Count || i < _sections[_heightCursor].StartRow) _heightCursor = 0;
        while (_heightCursor + 1 < _sections.Count && _sections[_heightCursor + 1].StartRow <= i) _heightCursor++;
        var s = _sections[_heightCursor];
        var local = i - s.StartRow;
        if (local == 0) return HeaderRowHeight;
        if (s.ConflictView != null) return ConflictBodyHeight(s);
        return s.RowSet.Rows.Count == 0 ? MessageRowHeight : LineHeight();
    }

    private float LineHeight() => _lineHeight > 0 ? _lineHeight : AssumedFontSize;

    private float BodyHeight(Section s)
    {
        if (s.Folded) return 0f;
        if (s.ConflictView != null) return ConflictBodyHeight(s);
        return s.RowSet.Rows.Count == 0 ? MessageRowHeight : s.RowSet.Rows.Count * LineHeight();
    }

    private float ConflictBodyHeight(Section s)
        => s.ConflictView!.MeasureHeight(ConflictPanelWidth(s.ConflictView));

    private float ConflictPanelWidth(View view)
        => Math.Max(CardViewportWidth(), view.MeasureWidth());

    private float SectionTopOffset(Section target)
    {
        var y = TopPadRowHeight;
        foreach (var s in _sections)
        {
            if (ReferenceEquals(s, target)) break;
            y += HeaderRowHeight + BodyHeight(s);
        }
        return y;
    }

    // ---- lazy loading ----

    // Creates the diff view models for every unloaded, unfolded section within the load margin.
    // Called each draw; a no-op once the neighbourhood is loaded.
    private void EnsureVisibleLoaded()
    {
        if (_sections.Count == 0) return;
        var pos = _list.Position;
        if (pos.Height <= 0) return;

        var top = _list.ScrollY - LoadMarginPx;
        var bottom = _list.ScrollY + pos.Height + LoadMarginPx;
        var y = TopPadRowHeight;
        foreach (var s in _sections)
        {
            var h = HeaderRowHeight + BodyHeight(s);
            if (y > bottom) break;
            if (y + h >= top && !s.Folded && s.Diff == null) StartLoad(s);
            y += h;
        }
    }

    private void StartLoad(Section s)
    {
        var diff = _details.CreateFileDiff(s.File.Path);
        if (diff == null) return;
        s.Diff = diff;
        // Fires immediately with the current state, then on load / highlight / expansion updates.
        s.Subscription = diff.Diff.RenderState.Subscribe(state => OnSectionRender(s, state));
        // The per-hunk index states repaint the action pills; nothing else re-renders on them.
        s.MarksSubscription = diff.Diff.WorkingTreeHunkStates.Subscribe(_ => SetDirty());
    }

    private void OnSectionRender(Section s, DiffRenderState state)
    {
        var oldHeight = BodyHeight(s);
        var oldRowCount = s.RowSet.Rows.Count;
        s.Render = state;
        s.RowSet = DiffRowSet.Build(state, _loc);
        SyncConflictView(s);
        // Row indices moved under a selection in this file (a gap expanded, the diff arrived). A
        // same-shape re-emit — the syntax highlight attaching — leaves them valid.
        if (s.RowSet.Rows.Count != oldRowCount) ClearSelectionIn(s.File.Path);
        s.GutterWidth = ComputeGutterWidth(s);
        RecomputeNaturalWidth();
        var newHeight = BodyHeight(s);
        if (Math.Abs(newHeight - oldHeight) > 0.0001f)
            ReindexWithAnchor(s, oldHeight, newHeight);
        else
            SetDirty();
    }

    // Re-indexes after one section's body height changed, keeping the viewport anchored: content
    // that grew or shrank entirely above the visible top edge shifts the scroll offset by the
    // delta, and collapsing the section the viewport is inside snaps back to its header.
    private void ReindexWithAnchor(Section s, float oldHeight, float newHeight)
    {
        var sectionTop = SectionTopOffset(s);
        var oldBottom = sectionTop + HeaderRowHeight + oldHeight;
        var scroll = _list.ScrollY;
        RebuildIndex();
        if (oldBottom <= scroll + 0.5f)
            _list.SetScrollY(scroll + (newHeight - oldHeight));
        else if (newHeight < oldHeight && sectionTop < scroll)
            _list.SetScrollY(sectionTop);
        SetDirty();
    }

    private float ComputeGutterWidth(Section s)
    {
        var advance = _metricsResolved ? _monoAdvance : AssumedFontSize * FallbackMonoAdvanceRatio;
        return s.RowSet.GutterDigits * advance + 8f;
    }

    private void RecomputeNaturalWidth()
    {
        _naturalWidth = 0f;
        foreach (var s in _sections)
        {
            GrowNaturalWidth(s);
            if (s.ConflictView is { } conflict)
            {
                var w = conflict.MeasureWidth();
                if (w > _naturalWidth) _naturalWidth = w;
            }
        }
    }

    private void GrowNaturalWidth(Section s)
    {
        var advance = _metricsResolved ? _monoAdvance : AssumedFontSize * FallbackMonoAdvanceRatio;
        var gutters = s.RowSet.SingleGutter ? s.GutterWidth : s.GutterWidth * 2;
        var width = gutters + DiffRowPainter.GlyphColumnWidth
            + s.RowSet.MaxRowCells * advance + DiffRowPainter.BannerPaddingX;
        if (width > _naturalWidth) _naturalWidth = width;
    }

    private void SyncConflictView(Section s)
    {
        if (s.Render is not DiffRenderState.Conflict)
        {
            RemoveConflictView(s);
            return;
        }
        if (s.ConflictView != null || s.Diff == null) return;
        var scope = new Context(_ctx);
        scope.AddService(s.Diff.Diff);
        var view = new ConflictResolveView().BuildView(scope);
        view.ZIndex = 50;
        s.ConflictView = view;
        AddChildToSelf(view);
        SetDirty();
    }

    private void RemoveConflictView(Section s)
    {
        if (s.ConflictView == null) return;
        RemoveChildFromSelf(s.ConflictView);
        s.ConflictView = null;
        SetDirty();
    }

    private Section? ConflictSectionOf(View child)
    {
        foreach (var s in _sections)
            if (ReferenceEquals(s.ConflictView, child))
                return s;
        return null;
    }

    protected override void OnLayoutChild(in RectF position, View child)
    {
        if (ConflictSectionOf(child) is { } s)
        {
            child.IsVisible = !s.Folded;
            if (s.Folded) return;
            var viewport = CardViewportWidth();
            var width = ConflictPanelWidth(child);
            var height = child.MeasureHeight(width);
            if (width > _naturalWidth) _naturalWidth = width;
            child.LeftConstraint = CardLeft() - (width > viewport ? _scrollX : 0f);
            child.WidthConstraint = width;
            child.HeightConstraint = height;
            child.BottomConstraint = BodyRowTopY(s, 0) - height;
            child.LayoutSelf();
            return;
        }
        base.OnLayoutChild(position, child);
    }

    public override bool ClipsContent => true;

    protected override void OnDrawChildren(ICanvas c)
    {
        c.PushClip(Position);
        base.OnDrawChildren(c);
        c.PopClip();
    }

    // ---- folding / viewed / navigation ----

    private void SetFolded(Section s, bool folded)
    {
        if (s.Folded == folded) return;
        var oldHeight = BodyHeight(s);
        s.Folded = folded;
        ClearSelectionIn(s.File.Path);
        ReindexWithAnchor(s, oldHeight, BodyHeight(s));
    }

    // Drops a text selection that belonged to a file whose rows just moved or vanished.
    private void ClearSelectionIn(string path)
    {
        if (!Equals(_selection.Scope, path)) return;
        _selection.Clear();
    }

    private HashSet<string> CurrentViewedSet()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in _sections)
            if (_vm.IsFileViewed(s.File.Path))
                set.Add(s.File.Path);
        return set;
    }

    // Folds files as they become Viewed and unfolds them when un-viewed — diffed against the last
    // snapshot so a manual fold/unfold isn't fought over on unrelated toggles.
    private void SyncViewedFolds()
    {
        if (_sections.Count == 0) return;
        var current = CurrentViewedSet();
        foreach (var s in _sections)
        {
            var viewed = current.Contains(s.File.Path);
            if (viewed == _viewedSnapshot.Contains(s.File.Path)) continue;
            SetFolded(s, viewed);
        }
        _viewedSnapshot = current;
        SetDirty();
    }

    // Scrolls a file's header band exactly onto the pin line, unfolding it so the diff is
    // readable — explicit navigation means the reviewer wants to see it, viewed or not.
    private void ScrollToFile(string path)
    {
        if (!_byPath.TryGetValue(path, out var s)) return;
        SetFolded(s, false);
        SetScrollTarget(SectionTopOffset(s) - TopPadRowHeight);
    }

    // ---- sticky header ----

    // The header of the file being read rides the viewport top once its own band scrolls off,
    // so the reviewer always sees which file they're in and can mark it Viewed on reaching the
    // end. The next file's header pushes it out as it arrives (the push-off shrinks the band's
    // visible slice until the incoming band takes over seamlessly). Folded files never pin —
    // their band is all they have, and it scrolls off like any row.
    private (Section Section, RectF Band)? FindStickyHeader()
    {
        if (_sections.Count == 0) return null;
        var pos = _list.Position;
        if (pos.Height <= 0) return null;

        // The sticky candidate is the last section whose header band has scrolled past the pin
        // line (PanelPaddingY below the viewport top, so the resting top padding is preserved);
        // nextTop ends up at the content offset of the section after it.
        var pinY = _list.ScrollY + PanelPaddingY;
        Section? sticky = null;
        var nextTop = TopPadRowHeight;
        foreach (var s in _sections)
        {
            if (nextTop + SectionGap >= pinY) break;
            sticky = s;
            nextTop += HeaderRowHeight + BodyHeight(s);
        }
        if (sticky == null || sticky.Folded) return null;

        // The section's own end does the pushing — the band is shoved up at the rate its last
        // rows run out, so the gap band between files stays visible under the outgoing header
        // instead of the next header riding glued against it.
        var pushOff = Math.Max(0f, pinY + HeaderBandHeight - nextTop);
        if (pushOff >= HeaderBandHeight) return null;

        var band = new RectF(CardLeft(), pos.Top - PanelPaddingY + pushOff - HeaderBandHeight,
            CardViewportWidth(), HeaderBandHeight);
        return (sticky, band);
    }

    // A click on the pinned band acts like one on the real header: the trailing zone toggles the
    // file's mark, the full-file zone flips the body mode, anywhere else folds the file — which
    // also snaps the viewport back to its header.
    private void OnStickyHeaderClicked(Section s, PointF point)
    {
        if (IsInViewedZone(point))
            _vm.ToggleFileViewed(s.File.Path);
        else if (IsInFullFileZone(point) && HasFullFileToggle(s))
            ToggleFullFile(s);
        else
            SetFolded(s, true);
        _vm.ReportActiveFile(s.File.Path);
    }

    // The Viewed toggle sits on the header's trailing edge — the card's right in LTR, left in RTL.
    private bool IsInViewedZone(PointF point) => IsRtl
        ? point.X <= CardLeft() + ViewedZoneWidth
        : point.X >= CardRight() - ViewedZoneWidth;

    // The full-file toggle rides just inside the Viewed zone, mirrored the same way.
    private bool IsInFullFileZone(PointF point) => IsRtl
        ? point.X > CardLeft() + ViewedZoneWidth
            && point.X <= CardLeft() + ViewedZoneWidth + FullFileZoneWidth
        : point.X < CardRight() - ViewedZoneWidth
            && point.X >= CardRight() - ViewedZoneWidth - FullFileZoneWidth;

    // The header toggle needs a loaded, unfolded card: an unloaded section has no DiffViewModel to
    // flip, and a folded one has no visible body for the mode to mean anything.
    private static bool HasFullFileToggle(Section s) => s.Diff != null && !s.Folded;

    private static void ToggleFullFile(Section s) => s.Diff?.Diff.ToggleFullFile();

    // ---- hunk actions ----

    // Per-hunk actions exist only on the working-tree surface (where the mark is the staged
    // state) over a loaded, patchable HEAD→disk diff; the branch-review window (Viewed marks,
    // Commit/Range sides) never qualifies.
    private bool HasHunkButtons(Section s)
        => _vm.MarkKind == ReviewMarkKind.Staged
            && s.Render is DiffRenderState.Loaded { Result.Side: DiffSide.WorkingTree } loaded
            && HunkPatchBuilder.CanPatchHunk(loaded.Result);

    // Each hunk's pills come from its real index state (Stage flips to Unstage once its region is
    // captured); until that async pass lands, the full set with toast fallbacks.
    private static HunkAction[] HunkActionsFor(Section s, int hunkIndex)
        => HunkButtonBar.ActionsFor(s.Diff?.Diff.WorkingTreeHunkStates.Value, hunkIndex, DiffSide.WorkingTree);

    // The hunk under a point, in a section qualified for hunk actions. Null under the padding
    // strip and the pinned band — both own their own input.
    private (Section Section, int HunkIndex)? HunkAt(PointF point)
    {
        if (point.Y > _list.Position.Top - PanelPaddingY) return null;
        if (FindStickyHeader() is { } sticky && sticky.Band.ContainsPoint(point)) return null;
        var index = _list.RowIndexAt(point);
        if (index < 0) return null;
        var s = Locate(index, out var local);
        return s == null ? null : HunkAtRow(s, local);
    }

    private (Section Section, int HunkIndex)? HunkAtRow(Section s, int local)
    {
        if (local == 0 || s.RowSet.Rows.Count == 0) return null;
        if (!HasHunkButtons(s)) return null;
        var hunkIndex = s.RowSet.HunkIndexOf(local - 1);
        return hunkIndex < 0 ? null : (s, hunkIndex);
    }

    // The buttons ride the hunk's second row (clamped for single-row hunks), same as the
    // single-file pane.
    private static int ButtonRowFor(Section s, int hunkIndex)
        => HunkButtonBar.ButtonRowFor(s.RowSet.HunkRanges[hunkIndex]);

    // Screen Y of a body row's top edge (bottom-up coordinates).
    private float BodyRowTopY(Section s, int bodyRow)
        => _list.Position.Top + _list.ScrollY
            - (SectionTopOffset(s) + HeaderRowHeight + bodyRow * LineHeight());

    private HunkAction HitTestHunkButton(Section s, int hunkIndex, PointF point)
        => _buttonBar.HitTest(
            point,
            CardRight(),
            BodyRowTopY(s, ButtonRowFor(s, hunkIndex)),
            HunkActionsFor(s, hunkIndex));

    private void OnHunkPointerMove(PointF point)
    {
        if (HunkAt(point) is not { } hit)
        {
            SetHunkHover(null, -1, HunkAction.None);
            return;
        }
        SetHunkHover(hit.Section, hit.HunkIndex, HitTestHunkButton(hit.Section, hit.HunkIndex, point));
    }

    private void SetHunkHover(Section? s, int hunkIndex, HunkAction button)
    {
        if (ReferenceEquals(_hoveredHunkSection, s)
            && _hoveredHunkIndex == hunkIndex
            && _hoveredHunkButton == button) return;
        _hoveredHunkSection = s;
        _hoveredHunkIndex = hunkIndex;
        _hoveredHunkButton = button;
        SetDirty();
    }

    // ---- input ----

    // Scrolling moves the reader, not the selection: the tree's highlight stays on the file the user
    // actually picked. The sticky header derives the file being read from the scroll offset directly,
    // so it still follows along.
    private void OnScrolled() => NotifyScrollChanged(viewportFits: false);

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

    private void OnRowClicked(int index, InputModifiers modifiers, PointF point)
    {
        // Rows under the top padding strip are hidden; clicks there target nothing.
        if (point.Y > _list.Position.Top - PanelPaddingY) return;
        // The pinned header covers whatever row scrolled beneath it; it owns clicks there.
        if (FindStickyHeader() is { } sticky && sticky.Band.ContainsPoint(point))
        {
            OnStickyHeaderClicked(sticky.Section, point);
            return;
        }

        var s = Locate(index, out var local);
        if (s == null) return;

        if (local == 0)
        {
            // The row's top slice is the gap between cards — a click there targets nothing.
            if (!_list.TryGetRowRect(index, out var rowRect)) return;
            if (point.Y > rowRect.Top - SectionGap) return;
            // The trailing zone is the mark checkbox, then the full-file toggle; anywhere else on
            // the band toggles the fold.
            if (IsInViewedZone(point))
                _vm.ToggleFileViewed(s.File.Path);
            else if (IsInFullFileZone(point) && HasFullFileToggle(s))
                ToggleFullFile(s);
            else
                SetFolded(s, !s.Folded);
            _vm.ReportActiveFile(s.File.Path);
            return;
        }

        if (s.RowSet.Rows.Count == 0) return;

        if (HasHunkButtons(s) && s.Diff != null
            && s.RowSet.HunkIndexOf(local - 1) is var hunkIndex and >= 0)
        {
            switch (HitTestHunkButton(s, hunkIndex, point))
            {
                case HunkAction.Stage: s.Diff.Diff.StageHunk(hunkIndex); return;
                case HunkAction.Unstage: s.Diff.Diff.UnstageHunk(hunkIndex); return;
                case HunkAction.Discard: s.Diff.Diff.RequestDiscardHunk(hunkIndex); return;
            }
        }

        var row = s.RowSet.Rows[local - 1];
        if (DiffRowPainter.GapBarOf(row) is not { } gap) return;
        var contentLeft = CardLeft() - _scrollX;
        if (DiffRowPainter.ExpanderHit(gap, point.X - contentLeft) is { } dir)
            s.Diff?.Diff.ExpandGap(gap.GapIndex, dir);
    }

    // Right-click anywhere on a file's card — its header (pinned or not) or its diff rows — opens
    // the per-file menu (mark Viewed / not Viewed). The gap band and the padding rows belong to no
    // file, so they open nothing.
    private void OnRowContextRequested(int index, PointF point)
    {
        if (point.Y > _list.Position.Top - PanelPaddingY) return;
        Section? s;
        if (FindStickyHeader() is { } sticky && sticky.Band.ContainsPoint(point))
        {
            s = sticky.Section;
        }
        else
        {
            s = Locate(index, out var local);
            if (s != null && local == 0)
            {
                if (!_list.TryGetRowRect(index, out var rowRect)) return;
                if (point.Y > rowRect.Top - SectionGap) return;
            }
        }
        if (s == null) return;
        RepoBarContextMenu.Show(_ctx, point, _vm.BuildFileContextMenuItems(s.File.Path));
    }

    private MouseCursor CursorAt(PointF point)
    {
        if (point.Y > _list.Position.Top - PanelPaddingY) return MouseCursor.Default;
        if (FindStickyHeader() is { } sticky && sticky.Band.ContainsPoint(point))
            return MouseCursor.Hand;
        var index = _list.RowIndexAt(point);
        if (index < 0) return MouseCursor.Default;
        var s = Locate(index, out var local);
        if (s == null) return MouseCursor.Default;
        if (local == 0)
        {
            if (!_list.TryGetRowRect(index, out var rowRect)) return MouseCursor.Default;
            return point.Y > rowRect.Top - SectionGap ? MouseCursor.Default : MouseCursor.Hand;
        }
        if (s.RowSet.Rows.Count == 0) return MouseCursor.Default;
        if (HunkAtRow(s, local) is { } hit && HitTestHunkButton(hit.Section, hit.HunkIndex, point) != HunkAction.None)
            return MouseCursor.Hand;
        var row = s.RowSet.Rows[local - 1];
        if (DiffRowPainter.GapBarOf(row) != null) return MouseCursor.Hand;
        return row is DiffRow.Line ? MouseCursor.Text : MouseCursor.Default;
    }

    // ---- text selection ----

    DiffSelectionModel IDiffSelectionSurface.Selection => _selection;
    RectF IDiffSelectionSurface.SelectionViewport => _list.Position;
    void IDiffSelectionSurface.ScrollBy(float dy) => _list.SetScrollY(_list.ScrollY + dy);
    void IDiffSelectionSurface.RequestRedraw() => SetDirty();

    IReadOnlyList<DiffRow>? IDiffSelectionSurface.RowsOf(object? scope) =>
        scope is string path && _byPath.TryGetValue(path, out var s) && s.RowSet.Rows.Count > 0
            ? s.RowSet.Rows
            : null;

    // Everything that isn't a code line: the padding strips, the pinned band, a card header, the
    // gap-expander bars, and the hunk-action buttons, each of which already owns its click.
    bool IDiffSelectionSurface.IsInteractiveAt(PointF point)
    {
        if (point.Y > _list.Position.Top - PanelPaddingY) return true;
        if (FindStickyHeader() is { } sticky && sticky.Band.ContainsPoint(point)) return true;

        var index = _list.RowIndexAt(point);
        if (index < 0) return false;
        var s = Locate(index, out var local);
        if (s == null) return false;
        if (local == 0) return true;
        if (s.RowSet.Rows.Count > 0 && DiffRowPainter.GapBarOf(s.RowSet.Rows[local - 1]) != null) return true;
        return HunkAtRow(s, local) is { } hit && HitTestHunkButton(hit.Section, hit.HunkIndex, point) != HunkAction.None;
    }

    DiffTextHit? IDiffSelectionSurface.HitTestText(PointF point)
    {
        if (!_metricsResolved || point.Y > _list.Position.Top - PanelPaddingY) return null;
        if (FindStickyHeader() is { } sticky && sticky.Band.ContainsPoint(point)) return null;
        if (!_list.Position.ContainsPoint(point)) return null;

        var index = _list.RowIndexAt(point);
        if (index < 0) return null;
        var s = Locate(index, out var local);
        if (s == null || local == 0 || s.RowSet.Rows.Count == 0) return null;
        if (s.RowSet.Rows[local - 1] is not DiffRow.Line line) return null;

        return new DiffTextHit(s.File.Path, new DiffTextPos(local - 1, CharIndexAt(s, line.Text, point.X)));
    }

    DiffTextHit? IDiffSelectionSurface.ClampToScope(PointF point, object? scope)
    {
        var s = ResolveScope(point, scope);
        if (s == null || s.Folded || s.RowSet.Rows.Count == 0) return null;

        // Content-space y of the pointer, measured down from the top of the scrolling surface.
        var contentY = _list.Position.Top - point.Y + _list.ScrollY;
        var bodyTop = SectionTopOffset(s) + HeaderRowHeight;
        var row = (int)MathF.Floor((contentY - bodyTop) / LineHeight());
        row = Math.Clamp(row, 0, s.RowSet.Rows.Count - 1);

        var text = s.RowSet.Rows[row] is DiffRow.Line line ? line.Text : string.Empty;
        return new DiffTextHit(s.File.Path, new DiffTextPos(row, CharIndexAt(s, text, point.X)));
    }

    // A named scope pins the drag to its card however far the pointer strays; an unnamed one is
    // resolved from the point, which is how Select All picks the card under the cursor.
    private Section? ResolveScope(PointF point, object? scope)
    {
        if (scope is string path) return _byPath.GetValueOrDefault(path);
        var index = _list.RowIndexAt(point);
        if (index < 0) return null;
        var s = Locate(index, out var local);
        return s != null && local > 0 ? s : null;
    }

    private int CharIndexAt(Section s, string text, float x)
    {
        if (_monoAdvance <= 0) return 0;
        var origin = DiffRowPainter.LineTextOriginX(CardLeft() - _scrollX, s.GutterWidth, s.RowSet.SingleGutter);
        return DiffText.CharIndexAtCell(text, (x - origin) / _monoAdvance);
    }

    // ---- drawing ----

    protected override void OnDrawSelf(ICanvas c)
    {
        var pos = Position;
        var z = GetDrawZIndex();
        c.DrawRect(new DrawRectInputs
        {
            Position = pos,
            Style = new RectStyle { BackgroundColor = _theme.Palette.Surface },
            ZIndex = z,
        });

        EnsureMetrics(c);
        _buttonBar.EnsureMetrics(c);
        ClampHorizontalScroll();
        ReassertPendingScroll();
        EnsureVisibleLoaded();
        _selectionController.Tick();
        NotifyScrollChanged(viewportFits: false);
    }

    private void EnsureMetrics(ICanvas c)
    {
        if (_metricsResolved) return;
        _lineHeight = c.MeasureTextLineHeight(DiffRowPainter.MonoMetricsStyle);
        var measured = c.MeasureTextWidth("0", DiffRowPainter.MonoMetricsStyle);
        _monoAdvance = measured > 0 ? measured : AssumedFontSize * FallbackMonoAdvanceRatio;
        _painter.LineHeight = _lineHeight;
        _painter.MonoAdvance = _monoAdvance;
        _metricsResolved = true;

        // Re-derive everything the fallback advance seeded, then re-measure the offset table.
        foreach (var s in _sections)
            s.GutterWidth = ComputeGutterWidth(s);
        RecomputeNaturalWidth();
        _list.InvalidateRowHeights();
    }

    private void DrawRowAt(ICanvas c, RectF rowRect, int index, RowRenderState state, int z)
    {
        var s = Locate(index, out var local);
        if (s == null) return;

        if (local == 0)
        {
            DrawHeader(c, s, rowRect, state.IsHovered, z);
            return;
        }

        var cardLeft = rowRect.Left + PanelPaddingX;
        var cardWidth = Math.Max(0f, rowRect.Width - PanelPaddingX * 2);

        if (s.RowSet.Rows.Count == 0)
        {
            DrawMessageRow(c, s, rowRect, cardLeft, cardWidth, z);
        }
        else
        {
            var row = s.RowSet.Rows[local - 1];
            DiffRowSelection? selection = null;
            if (row is DiffRow.Line line
                && _selection.TryRowSpan(s.File.Path, local - 1, line.Text.Length, out var span))
                selection = span;

            _painter.DrawRow(c, row, new DiffRowPaint(
                cardLeft - _scrollX,
                rowRect.Bottom,
                ContentWidth(),
                s.GutterWidth,
                s.RowSet.SingleGutter,
                ExpanderHovered: state.IsHovered && DiffRowPainter.GapBarOf(row) != null,
                Viewport: _list.Position,
                Z: z,
                Selection: selection));

            var hunkIndex = s.RowSet.HunkIndexOf(local - 1);
            if (hunkIndex >= 0 && hunkIndex == _hoveredHunkIndex
                && ReferenceEquals(s, _hoveredHunkSection) && HasHunkButtons(s))
            {
                DrawHunkOutlineForRow(c, s, cardLeft, cardWidth, rowRect, local - 1, hunkIndex, z + 5);
                if (local - 1 == ButtonRowFor(s, hunkIndex))
                    _buttonBar.Draw(
                        c, CardRight(), rowRect.Top,
                        HunkActionsFor(s, hunkIndex),
                        _hoveredHunkButton,
                        _theme.DiffHunkButton,
                        z + 7);
            }
        }

        // Card outline: 1px sides on every body row, closed by a bottom edge on the last one.
        // Drawn above the row content (long scrolled lines pass beneath; the margin overlay masks
        // whatever escapes the card).
        DrawCardSides(c, cardLeft, cardWidth, rowRect.Bottom, rowRect.Height, z + 6);
        if (local == s.BodyRows)
        {
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(cardLeft, rowRect.Bottom, cardWidth, 1f),
                Style = new RectStyle { BackgroundColor = _theme.Palette.Border },
                ZIndex = z + 6,
            });
        }
    }

    // The hovered hunk's outline, drawn per row against the card edges: 1px sides every row,
    // closed by a top edge on the hunk's first row and a bottom edge on its last.
    private void DrawHunkOutlineForRow(
        ICanvas c, Section s, float cardLeft, float cardWidth, RectF rowRect, int bodyRow, int hunkIndex, int z)
    {
        var range = s.RowSet.HunkRanges[hunkIndex];
        var style = new RectStyle { BackgroundColor = _theme.DiffContent.HunkOutline };
        var right = cardLeft + cardWidth - 1f;

        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(cardLeft, rowRect.Bottom, 1f, rowRect.Height),
            Style = style,
            ZIndex = z,
        });
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(right, rowRect.Bottom, 1f, rowRect.Height),
            Style = style,
            ZIndex = z,
        });

        if (bodyRow == range.FirstRow)
        {
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(cardLeft, rowRect.Top - 1f, cardWidth, 1f),
                Style = style,
                ZIndex = z,
            });
        }
        if (bodyRow == range.LastRow)
        {
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(cardLeft, rowRect.Bottom, cardWidth, 1f),
                Style = style,
                ZIndex = z,
            });
        }
    }

    private void DrawCardSides(ICanvas c, float cardLeft, float cardWidth, float bottom, float height, int z)
    {
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(cardLeft, bottom, 1f, height),
            Style = new RectStyle { BackgroundColor = _theme.Palette.Border },
            ZIndex = z,
        });
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(cardLeft + cardWidth - 1f, bottom, 1f, height),
            Style = new RectStyle { BackgroundColor = _theme.Palette.Border },
            ZIndex = z,
        });
    }

    // A file's header: the gap band above (panel surface, untouched) and the card's header band —
    // fold chevron, status icon, path (dimmed once viewed), and the Viewed checkbox on the
    // trailing edge. The active file carries a leading accent bar so the tree's highlight has a
    // visible counterpart while scrolling. A folded card is just this band, closed by its own
    // bottom edge.
    private void DrawHeader(ICanvas c, Section s, RectF rowRect, bool hovered, int z)
    {
        var viewed = _vm.IsFileViewed(s.File.Path);
        var cardLeft = rowRect.Left + PanelPaddingX;
        var cardWidth = Math.Max(0f, rowRect.Width - PanelPaddingX * 2);
        var band = new RectF(cardLeft, rowRect.Bottom, cardWidth, HeaderBandHeight);

        c.DrawRect(new DrawRectInputs
        {
            Position = band,
            Style = new RectStyle
            {
                BackgroundColor = hovered ? _theme.RowSelection.FillHover : _theme.FileChangesSection.HeaderBackground,
                BorderColor = BorderColorStyle.All(_theme.Palette.Border),
                BorderSize = BorderSizeStyle.All(1),
            },
            ZIndex = z,
        });

        if (_vm.ActiveFile.Value == s.File.Path)
        {
            c.DrawRect(new DrawRectInputs
            {
                Position = Place(band, cardLeft, ActiveBarWidth),
                Style = new RectStyle { BackgroundColor = _theme.RowSelection.AccentBar },
                ZIndex = z + 1,
            });
        }

        var x = cardLeft + HeaderPaddingX;
        HeaderGlyphStyle.TextColor = _theme.Palette.TextSecondary;
        c.DrawText(new DrawTextInputs
        {
            Position = Place(band, x, ChevronWidth),
            Text = s.Folded
                ? IsRtl ? LucideIcons.ChevronLeft : LucideIcons.ChevronRight
                : LucideIcons.ChevronDown,
            Style = HeaderGlyphStyle,
            ZIndex = z + 2,
        });
        x += ChevronWidth + 6f;

        HeaderGlyphStyle.TextColor = _theme.FileChangeRow.StatusColor(s.File.Status);
        c.DrawText(new DrawTextInputs
        {
            Position = Place(band, x, StatusIconWidth),
            Text = FileChangeFormatting.StatusIcon(s.File.Status),
            Style = HeaderGlyphStyle,
            ZIndex = z + 2,
        });
        x += StatusIconWidth + 8f;

        var zoneLeft = cardLeft + cardWidth - ViewedZoneWidth;
        var showFullFileToggle = HasFullFileToggle(s);
        var pathRight = showFullFileToggle ? zoneLeft - FullFileZoneWidth : zoneLeft;
        var textWidth = Math.Max(0f, pathRight - x - HeaderPaddingX);
        if (textWidth > 0)
        {
            // A viewed file is "done" — dim its path (half alpha) so the eye skips to what's left.
            var color = _theme.Palette.TextPrimary;
            HeaderPathStyle.TextColor = viewed ? Dim(color) : color;
            var text = TextEllipsis.Truncate(c, FileChangeFormatting.FormatPath(s.File), HeaderPathStyle, textWidth);
            c.DrawText(new DrawTextInputs
            {
                Position = Place(band, x, textWidth),
                Text = text,
                Style = HeaderPathStyle,
                ZIndex = z + 2,
            });
        }

        if (showFullFileToggle)
        {
            // Same glyph and engaged tint as the diff pane header's full-file toggle.
            HeaderGlyphStyle.TextColor = s.Diff!.Diff.Mode.Value == DiffViewMode.FullFile
                ? _theme.DiffView.HeaderToggleActive
                : _theme.Palette.TextSecondary;
            c.DrawText(new DrawTextInputs
            {
                Position = Place(band, zoneLeft - FullFileZoneWidth, FullFileZoneWidth),
                Text = LucideIcons.FileText,
                Style = HeaderGlyphStyle,
                ZIndex = z + 2,
            });
        }

        DrawViewedCheckbox(c, s.File.Path, band, zoneLeft, viewed, z);
    }

    // The mark checkbox on a header's trailing edge: glyph + label, success-tinted once checked. A
    // partially staged file gets the indeterminate glyph in the warning tint — it has staged content
    // and unstaged edits on top, so it is neither.
    private void DrawViewedCheckbox(ICanvas c, string path, RectF band, float zoneLeft, bool viewed, int z)
    {
        var partial = !viewed && _vm.IsFilePartiallyMarked(path);
        var (glyph, viewedColor) = (viewed, partial) switch
        {
            (true, _) => (LucideIcons.CheckSquare, _theme.Status.Success),
            (_, true) => (LucideIcons.MinusSquare, _theme.Status.Warning),
            _ => (LucideIcons.Square, _theme.Palette.TextSecondary),
        };
        HeaderGlyphStyle.TextColor = viewedColor;
        c.DrawText(new DrawTextInputs
        {
            Position = Place(band, zoneLeft, 18f),
            Text = glyph,
            Style = HeaderGlyphStyle,
            ZIndex = z + 2,
        });
        ViewedLabelStyle.TextColor = viewedColor;
        c.DrawText(new DrawTextInputs
        {
            Position = Place(band, zoneLeft + 22f, ViewedZoneWidth - 22f - HeaderPaddingX),
            Text = MarkLabel(),
            Style = ViewedLabelStyle,
            ZIndex = z + 2,
        });
    }

    // Reflects a header element's horizontal extent across the band when the UI is right-to-left,
    // so the left-origin header layout mirrors (chevron and status to the right, the Viewed zone on
    // the left) without rewriting it. In-box text right-aligns via the canvas text base.
    private RectF Place(in RectF band, float left, float width) => IsRtl
        ? new RectF(band.Left + band.Right - left - width, band.Bottom, width, band.Height)
        : new RectF(left, band.Bottom, width, band.Height);

    // Checking a file's box marks it viewed on a branch review and stages it on the working-tree
    // review, so the checkbox label says which.
    private string MarkLabel() => _vm.MarkKind == ReviewMarkKind.Staged
        ? _loc.Strings.Value.ReviewStaged
        : _loc.Strings.Value.ReviewViewed;

    // The single body row of an unloaded / binary / errored / empty section: the card's body
    // surface with a muted message.
    private void DrawMessageRow(ICanvas c, Section s, RectF rowRect, float cardLeft, float cardWidth, int z)
    {
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(cardLeft, rowRect.Bottom, cardWidth, rowRect.Height),
            Style = new RectStyle { BackgroundColor = _theme.DiffContent.Background },
            ZIndex = z,
        });

        var str = _loc.Strings.Value;
        var (text, color) = s.Render switch
        {
            null => (str.CommonLoading, _theme.DiffContent.PlaceholderText),
            DiffRenderState.Placeholder p => (p.Text, _theme.DiffContent.PlaceholderText),
            DiffRenderState.Conflict => (string.Empty, _theme.DiffContent.PlaceholderText),
            DiffRenderState.Loaded l when l.Result.ErrorMessage != null => (l.Result.ErrorMessage, _theme.DiffContent.ErrorText),
            DiffRenderState.Loaded l when l.Result.IsBinary => (str.DiffBinaryNotShown, _theme.DiffContent.PlaceholderText),
            DiffRenderState.Loaded => (str.DiffNoChanges, _theme.DiffContent.PlaceholderText),
            _ => (str.CommonLoading, _theme.DiffContent.PlaceholderText),
        };
        if (text.Length == 0) return;
        MessageStyle.TextColor = color;
        c.DrawText(new DrawTextInputs
        {
            Position = new RectF(cardLeft + HeaderPaddingX * 2, rowRect.Bottom,
                Math.Max(0f, cardWidth - HeaderPaddingX * 4), rowRect.Height),
            Text = text,
            Style = MessageStyle,
            ZIndex = z + 1,
        });
    }

    // Repaints the margin strips beside the cards so horizontally-scrolled diff rows never bleed
    // past the card edges. Runs from the overlay child, whose elevated ZIndex puts it above all
    // row content while staying inside the panel.
    private void DrawMargins(ICanvas c, int z)
    {
        var pos = _list.Position;
        if (pos.Width <= 0 || pos.Height <= 0) return;
        var style = new RectStyle { BackgroundColor = _theme.Palette.Surface };
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(pos.Left, pos.Bottom, PanelPaddingX, pos.Height),
            Style = style,
            ZIndex = z,
        });
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(pos.Right - PanelPaddingX, pos.Bottom, PanelPaddingX, pos.Height),
            Style = style,
            ZIndex = z,
        });
    }

    // The pinned copy of the active file's header, drawn at the viewport top over the scrolled
    // rows. Its fake row rect puts the band's bottom where FindStickyHeader pinned it; the clip
    // hides the slice a push-off shoves past the viewport edge. No hover — the list's hover state
    // belongs to the row underneath.
    private void DrawStickyHeader(ICanvas c, int z)
    {
        if (FindStickyHeader() is not { } sticky) return;
        var pos = _list.Position;
        c.PushClip(pos);
        var rowRect = new RectF(pos.Left, sticky.Band.Bottom, pos.Width, HeaderRowHeight);
        DrawHeader(c, sticky.Section, rowRect, hovered: false, z);
        c.PopClip();
    }

    // The padding strip above the pin line stays panel surface while content scrolls beneath it,
    // so the sticky header keeps the resting layout's top breathing room. Drawn above the sticky
    // band: a pushed-off header slides up under the padding, not over it.
    private void DrawTopPad(ICanvas c, int z)
    {
        var pos = _list.Position;
        if (pos.Width <= 0 || pos.Height <= 0) return;
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(pos.Left, pos.Top - PanelPaddingY, pos.Width, PanelPaddingY),
            Style = new RectStyle { BackgroundColor = _theme.Palette.Surface },
            ZIndex = z,
        });
    }

    // Halves a packed 0xAARRGGBB color's alpha, leaving RGB intact — the "viewed/done" dim.
    private static uint Dim(uint color) => (color & 0x00FFFFFFu) | (0x80u << 24);

    private sealed class PanelWheelController : KeyboardMouseController
    {
        private readonly ReviewDiffListView _owner;

        public PanelWheelController(ReviewDiffListView owner) => _owner = owner;

        public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
        {
            var list = _owner._list;
            if (e.DeltaY != 0f)
                list.SetScrollY(list.ScrollY - e.DeltaY * list.ScrollWheelStep);
            if (e.DeltaX != 0f)
                _owner.OnHorizontalWheel(e.DeltaX);
            e.Consume();
        }
    }

    // Forwards pointer motion into the hunk hover state (outline + action pills). Clicks stay on
    // the list's RowClicked path, like the gap expanders.
    private sealed class HunkHoverController : KeyboardMouseController
    {
        private readonly ReviewDiffListView _owner;

        public HunkHoverController(ReviewDiffListView owner) => _owner = owner;

        public override void OnMouseMoved(ref MouseMoveEvent e) => _owner.OnHunkPointerMove(e.Mouse.Point);

        public override void OnMouseExit(ref MouseExitEvent e) => _owner.SetHunkHover(null, -1, HunkAction.None);
    }

    // Logic-free sibling of the row list: its raised ZIndex lets the margin strips and the sticky
    // header paint over row content, which the list itself (rows draw above their own view)
    // cannot do.
    private sealed class PanelOverlay : View
    {
        private readonly ReviewDiffListView _owner;

        public PanelOverlay(ReviewDiffListView owner)
        {
            _owner = owner;
            ZIndex = 100;
        }

        protected override void OnDrawSelf(ICanvas c)
        {
            var z = GetDrawZIndex();
            _owner.DrawMargins(c, z);
            _owner.DrawStickyHeader(c, z + 2);
            _owner.DrawTopPad(c, z + 8);
        }
    }

    // ---- scrolling ----

    private float ContentWidth() => Math.Max(CardViewportWidth(), _naturalWidth);

    private void ClampHorizontalScroll()
    {
        var maxX = Math.Max(0f, ContentWidth() - CardViewportWidth());
        if (_scrollX < 0f) _scrollX = 0f;
        else if (_scrollX > maxX) _scrollX = maxX;
    }

    private void SetScrollTarget(float y)
    {
        _pendingScrollY = y;
        _pendingScrollFrames = 8;
        _list.SetScrollY(y);
    }

    private void ReassertPendingScroll()
    {
        if (_pendingScrollY is not float want) return;
        var max = Math.Max(0f, _list.ContentHeight - _list.Position.Height);
        var clamped = Math.Clamp(want, 0f, max);
        if (Math.Abs(_list.ScrollY - clamped) <= 0.5f || --_pendingScrollFrames < 0)
        {
            _pendingScrollY = null;
            return;
        }
        _list.SetScrollY(clamped);
    }

    public void SetVerticalNormalizedScrollPosition(float normalized)
    {
        var range = _list.ContentHeight - Position.Height;
        if (range <= 0) { _list.SetScrollY(0f); }
        else { _list.SetScrollY(Math.Clamp(normalized, 0f, 1f) * range); }
    }

    public void SetHorizontalNormalizedScrollPosition(float normalized)
    {
        var range = ContentWidth() - CardViewportWidth();
        if (range <= 0) { _scrollX = 0; }
        else { _scrollX = Math.Clamp(normalized, 0f, 1f) * range; }
        SetDirty();
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
            var contentH = _list.ContentHeight;
            var contentW = ContentWidth();
            var vph = Position.Height;
            var vpw = CardViewportWidth();

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

    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _dispose;
        public ActionDisposable(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
