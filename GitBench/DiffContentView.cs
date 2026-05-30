using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;

namespace GitGui;

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
    private const float AssumedFontSize = 13f;
    // Fallback mono advance ratio if the canvas isn't available yet to measure a glyph.
    private const float FallbackMonoAdvanceRatio = 0.6f;

    private const float HunkButtonHeight = 18f;
    private const float HunkButtonPaddingX = 10f;
    private const float HunkButtonGap = 6f;
    private const float HunkButtonsMarginRight = 8f;
    private const float HunkButtonsTopInset = 4f;
    private const float HunkButtonFontSize = 11f;
    private const float HunkOutlineThickness = 1f;

    // Shared style instances. TextStyle is a class so DrawTextInputs holds a reference; we
    // mutate the few that need per-row recoloring (banner/glyph/line text in the row body)
    // on the UI thread between draw calls, so there's no aliasing concern.
    private static readonly TextStyle MonoMetricsStyle = new()
    {
        FontFamily = DiffOptions.MonoFontFamily,
        FontSize = AssumedFontSize,
    };
    private static readonly TextStyle MonoStartStyle = new()
    {
        FontFamily = DiffOptions.MonoFontFamily,
        FontSize = AssumedFontSize,
        VerticalAlignment = TextAlignment.Center,
    };
    private static readonly TextStyle MonoEndStyle = new()
    {
        FontFamily = DiffOptions.MonoFontFamily,
        FontSize = AssumedFontSize,
        HorizontalAlignment = TextAlignment.End,
        VerticalAlignment = TextAlignment.Center,
    };
    private static readonly TextStyle MonoCenterStyle = new()
    {
        FontFamily = DiffOptions.MonoFontFamily,
        FontSize = AssumedFontSize,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
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

    public event Action<float>? VerticalScrollPositionChanged;
    public event Action<float>? HorizontalScrollPositionChanged;

    public float VerticalScale { get; private set; } = 1f;
    public float HorizontalScale { get; private set; } = 1f;

    private DiffContentStyles _styles = ThemeStyles.Dark.DiffContent;
    private DiffHunkButtonStyles _buttonStyles = ThemeStyles.Dark.DiffHunkButton;

    private DiffRenderState _renderState = new DiffRenderState.Placeholder("Select a file to view diff.");
    private readonly List<DiffRow> _rows = new();
    private int _maxRowChars;
    private float _gutterWidth;
    private float _lineHeight;
    private float _monoAdvance;
    private bool _metricsResolved;

    private readonly List<HunkRowRange> _hunkRanges = new();
    private int[] _rowToHunk = Array.Empty<int>();
    private DiffSide _diffSide;
    private bool _hunksPatchable;
    private int _hoveredHunkIndex = -1;
    private HunkAction _hoveredButton = HunkAction.None;
    private float _stageBtnTextWidth;
    private float _unstageBtnTextWidth;
    private float _discardBtnTextWidth;
    private bool _buttonMetricsResolved;

    public Action<int>? OnStageHunk { get; set; }
    public Action<int>? OnUnstageHunk { get; set; }
    public Action<int>? OnDiscardHunk { get; set; }

    private readonly VirtualRowListView _list;

    private float _scrollX;
    private float _lastNormalizedY;
    private float _lastNormalizedX;
    // Sentinel start so the very first NotifyScrollChanged fires the event even when the
    // computed scale equals 1. The scrollbar thumb's built-in default is Scale=0.5 with
    // PreferredHeight=12 — without an explicit "scale=1, hide" message it stays visible
    // at half width until something else (a file that genuinely needs scroll) forces a
    // change. -1f is impossible for a real scale.
    private float _lastVerticalScale = -1f;
    private float _lastHorizontalScale = -1f;

    public DiffContentView()
    {
        _list = new VirtualRowListView
        {
            RowHeight = AssumedFontSize, // placeholder until canvas-derived metrics resolve
            ItemBuilder = DrawDiffRowAt,
        };
        _list.ScrollChanged += () => NotifyScrollChanged(viewportFits: false);
        _list.HorizontalWheelHandler = OnHorizontalWheel;

        AddChildToSelf(_list);
        _list.UseController(_ => new VirtualRowListController(_list));
        this.UseController(_ => new DiffMouseController(this), EventPhaseFilter.Capture);

        this.BindThemed(s =>
        {
            _styles = s.DiffContent;
            _buttonStyles = s.DiffHunkButton;
            SetDirty();
        });
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
        _renderState = state;
        _rows.Clear();
        _hunkRanges.Clear();
        _rowToHunk = Array.Empty<int>();
        _maxRowChars = 0;
        _scrollX = 0;
        _hoveredHunkIndex = -1;
        _hoveredButton = HunkAction.None;
        _hunksPatchable = false;
        _diffSide = DiffSide.Unstaged;
        // Metrics depend only on font, not content, but content width depends on metrics;
        // a fresh model forces a recompute on next draw.
        _metricsResolved = false;

        if (state is DiffRenderState.Loaded loaded)
        {
            _diffSide = loaded.Result.Side;
            _hunksPatchable = HunkPatchBuilder.CanPatchHunk(loaded.Result);
            FlattenRows(loaded.Result);
        }

        _list.ItemCount = _rows.Count;
        _list.SetScrollY(0f);
        _list.NotifyItemsChanged();
        SetDirty();
    }

    private void FlattenRows(DiffResult r)
    {
        if (r.ErrorMessage != null) return;
        if (r.IsBinary) return;
        if (r.Hunks.Count == 0 && !r.IsModeOnly && r.OldPath == null) return;

        if (r.OldPath != null)
            AddBanner($"Renamed: {r.OldPath} → {r.Path}");
        if (r.IsModeOnly)
            AddBanner($"Mode: {FormatMode(r.OldMode)} → {FormatMode(r.NewMode)}");

        int maxOld = 0, maxNew = 0, totalLines = 0;
        foreach (var h in r.Hunks)
        {
            foreach (var l in h.Lines)
            {
                if (l.OldLineNumber is int o && o > maxOld) maxOld = o;
                if (l.NewLineNumber is int n && n > maxNew) maxNew = n;
            }
            totalLines += h.Lines.Count;
        }
        // Gutter widths picked from max digit count, same heuristic as the old code.
        var digits = Math.Max(1, Math.Max(DigitCount(maxOld), DigitCount(maxNew)));
        _gutterWidth = digits * AssumedFontSize * FallbackMonoAdvanceRatio + 8f;

        for (var i = 0; i < r.Hunks.Count; i++)
        {
            var h = r.Hunks[i];
            var separatorRowIndex = _rows.Count;
            var range = $"@@ -{h.OldStart},{h.OldLines} +{h.NewStart},{h.NewLines} @@";
            _rows.Add(new DiffRow.HunkSeparator(range, string.IsNullOrEmpty(h.Header) ? null : h.Header));
            var sepChars = range.Length + (h.Header?.Length ?? 0) + 2;
            if (sepChars > _maxRowChars) _maxRowChars = sepChars;

            foreach (var l in h.Lines)
            {
                var text = ExpandTabs(l.Text);
                var row = new DiffRow.Line(
                    l.Kind,
                    l.OldLineNumber?.ToString() ?? string.Empty,
                    l.NewLineNumber?.ToString() ?? string.Empty,
                    text,
                    text.Length);
                _rows.Add(row);
                if (text.Length > _maxRowChars) _maxRowChars = text.Length;
            }
            _hunkRanges.Add(new HunkRowRange(i, separatorRowIndex, _rows.Count - 1));
        }

        if (r.Truncated)
            AddBanner($"Diff truncated — only the first {totalLines} lines are shown.");

        _rowToHunk = new int[_rows.Count];
        Array.Fill(_rowToHunk, -1);
        foreach (var range in _hunkRanges)
            for (var i = range.FirstRow; i <= range.LastRow; i++)
                _rowToHunk[i] = range.HunkIndex;
    }

    private void AddBanner(string text)
    {
        _rows.Add(new DiffRow.Banner(text));
        if (text.Length > _maxRowChars) _maxRowChars = text.Length;
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
        // Worst case across row kinds: line rows go gutter|gutter|glyph|text; banner rows
        // are flush-left with horizontal padding. Take the max of both formulas.
        var lineWidth = _gutterWidth + _gutterWidth + GlyphColumnWidth + _maxRowChars * _monoAdvance + BannerPaddingX;
        var bannerWidth = BannerPaddingX * 2 + _maxRowChars * _monoAdvance;
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
            _stageBtnTextWidth = c.MeasureTextWidth("Stage", HunkButtonTextStyle);
            _unstageBtnTextWidth = c.MeasureTextWidth("Unstage", HunkButtonTextStyle);
            _discardBtnTextWidth = c.MeasureTextWidth("Discard", HunkButtonTextStyle);
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
            case DiffRenderState.Loaded loaded when loaded.Result.ErrorMessage != null:
                DrawPlaceholder(c, pos, loaded.Result.ErrorMessage, _styles.ErrorText, z + 1);
                NotifyScrollChanged(viewportFits: true);
                return;
            case DiffRenderState.Loaded loaded when loaded.Result.IsBinary:
                DrawPlaceholder(c, pos, "Binary file not shown", _styles.PlaceholderText, z + 1);
                NotifyScrollChanged(viewportFits: true);
                return;
            case DiffRenderState.Loaded when _rows.Count == 0:
                DrawPlaceholder(c, pos, "No textual changes", _styles.PlaceholderText, z + 1);
                NotifyScrollChanged(viewportFits: true);
                return;
        }

        EnsureMetrics(c);
        ClampHorizontalScroll();
        NotifyScrollChanged(viewportFits: false);
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
                DrawHunkSeparatorRow(c, s, rowLeft, rowBottom, rowWidth, z);
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

    private static string ButtonLabel(HunkAction action) => action switch
    {
        HunkAction.Stage => "Stage",
        HunkAction.Unstage => "Unstage",
        HunkAction.Discard => "Discard",
        _ => string.Empty,
    };

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
                    BorderRadius = BorderRadiusStyle.All(3),
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

    private void DrawHunkSeparatorRow(ICanvas c, DiffRow.HunkSeparator s, float left, float bottom, float width, int z)
    {
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(left, bottom, width, _lineHeight),
            Style = SolidBgStyle(_styles.SectionBackground),
            ZIndex = z,
        });

        var rangeWidth = s.Range.Length * _monoAdvance;
        var textX = left + BannerPaddingX;
        DrawMonoText(c, s.Range, textX, bottom, rangeWidth,
            _styles.HunkSeparatorRangeText, TextAlignment.Start, z + 1);

        if (s.Header != null)
        {
            var headerX = textX + rangeWidth + HunkHeaderGap;
            var headerWidth = Math.Max(0f, left + width - BannerPaddingX - headerX);
            if (headerWidth > 0)
                DrawMonoText(c, s.Header, headerX, bottom, headerWidth,
                    _styles.SectionMutedText, TextAlignment.Start, z + 1);
        }
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
        DrawMonoText(c, l.OldNumber, x, bottom, _gutterWidth,
            _styles.LineNumberText, TextAlignment.End, z + 1);
        x += _gutterWidth + 4f;
        DrawMonoText(c, l.NewNumber, x, bottom, _gutterWidth,
            _styles.LineNumberText, TextAlignment.End, z + 1);
        x += _gutterWidth + 4f;
        DrawMonoText(c, glyph, x, bottom, GlyphColumnWidth, glyphColor, TextAlignment.Center, z + 1);
        x += GlyphColumnWidth + 4f;

        var textWidth = Math.Max(0f, left + width - x);
        DrawMonoText(c, l.Text, x, bottom, textWidth,
            _styles.LineText, TextAlignment.Start, z + 1);
    }

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
        SetHunkHover(-1, HunkAction.None);
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

    private static string ExpandTabs(string s)
    {
        if (s.IndexOf('\t') < 0) return s;
        return s.Replace("\t", TabReplacement);
    }

    private static readonly string TabReplacement = new(' ', DiffOptions.TabWidth);

    private static string FormatMode(int? mode)
        => mode is int m ? Convert.ToString(m, 8).PadLeft(6, '0') : "-";
}
