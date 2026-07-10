using GitBench.Controls;
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

internal sealed class DiffContentView : View, IScrollableContent, IDiffSelectionSurface
{
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

    public event Action<float>? VerticalScrollPositionChanged;
    public event Action<float>? HorizontalScrollPositionChanged;

    public float VerticalScale { get; private set; } = 1f;
    public float HorizontalScale { get; private set; } = 1f;

    private DiffContentStyles _styles = ThemeStyles.Dark.DiffContent;
    private DiffHunkButtonStyles _buttonStyles = ThemeStyles.Dark.DiffHunkButton;

    private DiffRenderState _renderState = new DiffRenderState.Placeholder("Select a file to view diff.");
    private DiffRowSet _rowSet = DiffRowSet.Empty;
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
    private readonly DiffSelectionModel _selection = new();
    private readonly DiffSelectionController _selectionController;

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
        _painter = new DiffRowPainter(_loc);

        _list = new VirtualRowListView
        {
            RowHeight = AssumedFontSize, // placeholder until canvas-derived metrics resolve
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
        var prevRowCount = _rowSet.Rows.Count;

        _renderState = state;
        _scrollX = 0;
        _hoveredHunkIndex = -1;
        _hoveredButton = HunkAction.None;
        _hoveredExpanderRow = -1;
        _hunksPatchable = false;
        _diffSide = DiffSide.Unstaged;
        // Metrics depend only on font, not content, but content width depends on metrics;
        // a fresh model forces a recompute on next draw.
        _metricsResolved = false;

        _rowSet = DiffRowSet.Build(state, _loc);
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

        _list.ItemCount = _rowSet.Rows.Count;
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
        var rows = _rowSet.Rows;
        if (_lineHeight <= 0 || rows.Count == 0) return false;
        var topIndex = Math.Clamp((int)(_list.ScrollY / _lineHeight), 0, rows.Count - 1);
        for (var i = topIndex; i < rows.Count; i++)
        {
            if (rows[i] is DiffRow.Line l && l.NewNumber.Length > 0 && int.TryParse(l.NewNumber, out var n))
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
        if (_lineHeight <= 0 || _rowSet.Rows.Count == 0) return;
        var rowIndex = FindRowForNewLine(lineNumber);
        if (rowIndex < 0) return;
        SetScrollTarget(Math.Max(0, rowIndex - leadIn) * _lineHeight);
    }

    // First row whose new-side line number equals lineNumber; falls back to the closest preceding
    // numbered row so a target with no exact row still lands sensibly. New numbers are monotonic
    // in row order in both modes, so a single forward scan suffices.
    private int FindRowForNewLine(int lineNumber)
    {
        var rows = _rowSet.Rows;
        var best = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not DiffRow.Line l || l.NewNumber.Length == 0) continue;
            if (!int.TryParse(l.NewNumber, out var n)) continue;
            if (n == lineNumber) return i;
            if (n < lineNumber) best = i;
        }
        return best;
    }

    private float ContentHeight()
    {
        if (_lineHeight <= 0) return 0f;
        return _rowSet.Rows.Count * _lineHeight;
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
        var lineWidth = gutters + DiffRowPainter.GlyphColumnWidth
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
        if (!_buttonMetricsResolved)
        {
            var s = _loc.Strings.Value;
            _stageBtnTextWidth = c.MeasureTextWidth(s.DiffHunkStage, HunkButtonTextStyle);
            _unstageBtnTextWidth = c.MeasureTextWidth(s.DiffHunkUnstage, HunkButtonTextStyle);
            _discardBtnTextWidth = c.MeasureTextWidth(s.DiffHunkDiscard, HunkButtonTextStyle);
            _buttonMetricsResolved = _stageBtnTextWidth > 0;
        }

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
        ReassertPendingScroll();
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

    private float ClampScrollTarget(float y)
    {
        var max = Math.Max(0f, _rowSet.Rows.Count * _lineHeight - _list.Position.Height);
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
            && _selection.TryRowSpan(null, rowIndex, line.Text.Length, out var span))
            selection = span;

        _painter.DrawRow(c, rows[rowIndex], new DiffRowPaint(
            rowLeft, rowRect.Bottom, rowWidth, _gutterWidth, _rowSet.SingleGutter,
            ExpanderHovered: rowIndex == _hoveredExpanderRow,
            Viewport: _list.Position,
            Z: z,
            Selection: selection));

        if (isHoveredHunk)
            DrawHunkOutlineForRow(c, rowRect, rowIndex, hunkIndex, z + 5);
        if (showButtons)
            DrawHunkButtons(c, rowRect, hunkIndex, z + 6);
    }

    private int ButtonRowFor(int hunkIndex)
    {
        if (hunkIndex < 0 || hunkIndex >= _rowSet.HunkRanges.Count) return -1;
        var range = _rowSet.HunkRanges[hunkIndex];
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

        if (!HasHunkButtons()) { SetHunkHover(-1, HunkAction.None); return; }

        var listPos = _list.Position;
        if (!listPos.ContainsPoint(point)) { SetHunkHover(-1, HunkAction.None); return; }

        var rowIndex = HitTestListRow(point);
        var hunkIndex = _rowSet.HunkIndexOf(rowIndex);
        var button = HunkAction.None;
        if (hunkIndex >= 0)
            button = HitTestButton(point, ButtonRowFor(hunkIndex));
        SetHunkHover(hunkIndex, button);
    }

    public void OnHunkPointerExit()
    {
        SetExpanderHover(-1);
        SetHunkHover(-1, HunkAction.None);
    }

    public bool TryClickExpander(PointF point)
    {
        if (HitTestExpander(point) is not { } hit) return false;
        OnExpandGap?.Invoke(hit.GapIndex, hit.Dir);
        return true;
    }

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
        var idx = RawRowIndex(point);
        if (idx < 0 || idx >= _rowSet.Rows.Count) return -1;
        return idx;
    }

    // The row a point falls on, unbounded: negative above the first row, past the count below the
    // last. Clamping it is how a drag that runs off either end keeps extending to the extremes.
    private int RawRowIndex(PointF point)
    {
        var distFromTop = _list.Position.Top - point.Y;
        return (int)MathF.Floor((distFromTop + _list.ScrollY) / _lineHeight);
    }

    // ---- text selection ----

    // One file, so every position shares the single implicit scope: null.
    DiffSelectionModel IDiffSelectionSurface.Selection => _selection;
    RectF IDiffSelectionSurface.SelectionViewport => _list.Position;
    IReadOnlyList<DiffRow>? IDiffSelectionSurface.RowsOf(object? scope) => _rowSet.Rows;
    void IDiffSelectionSurface.ScrollBy(float dy) => _list.SetScrollY(_list.ScrollY + dy);
    void IDiffSelectionSurface.RequestRedraw() => SetDirty();

    bool IDiffSelectionSurface.IsInteractiveAt(PointF point)
    {
        if (HitTestExpander(point) != null) return true;
        if (!HasHunkButtons()) return false;
        var hunkIndex = _rowSet.HunkIndexOf(HitTestListRow(point));
        return hunkIndex >= 0 && HitTestButton(point, ButtonRowFor(hunkIndex)) != HunkAction.None;
    }

    DiffTextHit? IDiffSelectionSurface.HitTestText(PointF point)
    {
        if (_lineHeight <= 0 || !_list.Position.ContainsPoint(point)) return null;
        var rowIndex = HitTestListRow(point);
        if (rowIndex < 0 || _rowSet.Rows[rowIndex] is not DiffRow.Line line) return null;
        return new DiffTextHit(null, new DiffTextPos(rowIndex, CharIndexAt(line.Text, point.X)));
    }

    DiffTextHit? IDiffSelectionSurface.ClampToScope(PointF point, object? scope)
    {
        if (_lineHeight <= 0 || _rowSet.Rows.Count == 0) return null;
        var rowIndex = Math.Clamp(RawRowIndex(point), 0, _rowSet.Rows.Count - 1);
        // A drag crossing a banner or a hunk bar keeps extending through it; those rows carry no
        // selectable text, so they contribute nothing to the copy.
        var text = _rowSet.Rows[rowIndex] is DiffRow.Line line ? line.Text : string.Empty;
        return new DiffTextHit(null, new DiffTextPos(rowIndex, CharIndexAt(text, point.X)));
    }

    private int CharIndexAt(string text, float x)
    {
        if (_monoAdvance <= 0) return 0;
        var origin = DiffRowPainter.LineTextOriginX(
            _list.Position.Left - _scrollX, _gutterWidth, _rowSet.SingleGutter);
        return DiffText.CharIndexAtCell(text, (x - origin) / _monoAdvance);
    }

    private MouseCursor CursorAt(PointF point)
    {
        if (((IDiffSelectionSurface)this).IsInteractiveAt(point)) return MouseCursor.Hand;
        return ((IDiffSelectionSurface)this).HitTestText(point) != null
            ? MouseCursor.Text
            : MouseCursor.Default;
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
}
