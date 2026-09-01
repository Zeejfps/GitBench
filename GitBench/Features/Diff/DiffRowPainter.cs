using GitBench.Controls;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Fonts;
using ZGF.Geometry;
using ZGF.Gui;

namespace GitBench.Features.Diff;

/// <summary>
/// Per-row paint parameters: the row's placement (Left is already horizontal-scroll adjusted,
/// Width is the full content width), the file's gutter geometry, whether the row's gap bar is
/// hovered, the list viewport (for clipping the tear pattern to visible segments), and the slice
/// of the row's text covered by the text selection, if any.
/// </summary>
internal readonly record struct DiffRowPaint(
    float Left,
    float Bottom,
    float Width,
    float GutterWidth,
    bool SingleGutter,
    bool ExpanderHovered,
    RectF Viewport,
    int Z,
    DiffRowSelection? Selection = null,
    bool FoldColumn = false,
    bool FoldHovered = false);

/// <summary>
/// Paints individual <see cref="DiffRow"/>s — banners, hunk separators, tears, and code lines
/// (backgrounds, gutters, intra-line emphasis, syntax-colored runs). Shared by
/// <see cref="DiffContentView"/> (the single-file pane) and the review window's stacked list so
/// diff rows render pixel-identically everywhere. Configure <see cref="Styles"/> and the font
/// metrics, then call <see cref="DrawRow"/> per visible row.
/// </summary>
internal sealed class DiffRowPainter
{
    public const float GlyphColumnWidth = 18f;
    public const float BannerPaddingX = 8f;
    private const float HunkHeaderGap = 12f;
    private const string HeaderEllipsis = "…";
    // Breathing space after each gutter column and after the kind glyph.
    private const float ColumnGap = 4f;
    // How far a selection runs past the last glyph on a row whose newline it swallows, so a
    // multi-line selection reads as continuous instead of stopping ragged at each line's end.
    private const float SelectionEolWidth = 0.5f;

    // Gap-expander icons on the separator bars: fixed-width clickable cells at the far left of
    // the bar, over the gutter columns. Draw and hit-test share this geometry.
    public const float ExpanderPadLeft = 4f;
    public const float ExpanderCellWidth = 22f;
    private const float ExpanderChipGap = 2f;

    private const float TearHalfPeriod = 9f;
    private const float TearAmplitude = 4.5f;
    private const float TearThickness = 1.25f;

    // Shared style instances. TextStyle is a class so DrawTextInputs holds a reference; the few
    // that need per-row recoloring are mutated on the UI thread between draw calls, so there's no
    // aliasing concern — including across painter consumers, which all draw on the same thread.
    // The code grid is pinned LTR (BaseDirection.Ltr): source lines and the line-number gutter are
    // a fixed left-origin monospace grid, so they must not follow the UI direction or right-align /
    // bidi-reorder when the locale is RTL — only the surrounding chrome mirrors.
    public static readonly TextStyle MonoMetricsStyle = new()
    {
        FontFamily = MonoFonts.Regular,
        FontSize = FontSize.Body,
        BaseDirection = BidiDirection.Ltr,
    };
    private static readonly TextStyle MonoStartStyle = new()
    {
        FontFamily = MonoFonts.Regular,
        FontSize = FontSize.Body,
        VerticalAlignment = TextAlignment.Center,
        BaseDirection = BidiDirection.Ltr,
    };
    private static readonly TextStyle MonoEndStyle = new()
    {
        FontFamily = MonoFonts.Regular,
        FontSize = FontSize.Body,
        HorizontalAlignment = TextAlignment.End,
        VerticalAlignment = TextAlignment.Center,
        BaseDirection = BidiDirection.Ltr,
    };
    private static readonly TextStyle MonoCenterStyle = new()
    {
        FontFamily = MonoFonts.Regular,
        FontSize = FontSize.Body,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
        BaseDirection = BidiDirection.Ltr,
    };
    private static readonly TextStyle ExpanderIconStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Body,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };

    private readonly ILocalizationService _loc;

    public DiffRowPainter(ILocalizationService loc) => _loc = loc;

    public DiffContentStyles Styles { get; set; } = ThemeStyles.Dark.DiffContent;
    public float LineHeight { get; set; }
    public float MonoAdvance { get; set; }

    public void DrawRow(ICanvas c, DiffRow row, in DiffRowPaint p)
    {
        switch (row)
        {
            case DiffRow.Banner b:
                DrawBannerRow(c, b, p);
                break;
            case DiffRow.HunkSeparator s:
                DrawHunkSeparatorRow(c, s, p);
                break;
            case DiffRow.Tear t:
                DrawTearRow(c, t, p);
                break;
            case DiffRow.Line l:
                DrawLineRow(c, l, p);
                break;
        }
    }

    /// <summary>
    /// The x where a line row's text begins: past the line-number gutter(s) and the +/- glyph
    /// column. The hit-test maps a pointer back to a character against this same origin, so the
    /// caret lands where the glyph is drawn.
    /// </summary>
    public static float LineTextOriginX(
        float left, float gutterWidth, bool singleGutter, bool foldColumn = false)
    {
        var gutters = singleGutter ? 1 : 2;
        return left + gutters * (gutterWidth + ColumnGap) + FoldColumnWidthOf(foldColumn)
            + GlyphColumnWidth + ColumnGap;
    }

    /// <summary>The chevron column's width, reserved for the whole surface or not at all.</summary>
    public static float FoldColumnWidthOf(bool foldColumn) => foldColumn ? FoldColumnWidth : 0f;

    public const float FoldColumnWidth = 14f;

    /// <summary>Whether a point, measured from the row's left edge, lands in the fold column.</summary>
    public static bool FoldHit(float xFromRowLeft, float gutterWidth, bool singleGutter)
    {
        var gutters = singleGutter ? 1 : 2;
        var start = gutters * (gutterWidth + ColumnGap);
        return xFromRowLeft >= start && xFromRowLeft < start + FoldColumnWidth;
    }

    /// <summary>The gap bar a row carries, or null for rows that aren't expander targets.</summary>
    public static GapBar? GapBarOf(DiffRow row) => row switch
    {
        DiffRow.HunkSeparator { Gap: { } g } => g,
        DiffRow.Tear t => t.Gap,
        _ => null,
    };

    /// <summary>
    /// The expander a click on a bar row targets, from the pointer x relative to the row's
    /// content-space left edge. A single-expander bar (every bar in practice) is clickable across
    /// its whole width, so the tiny arrow isn't the only target; a multi-expander bar keeps
    /// per-icon cells so each arrow still maps to its own direction.
    /// </summary>
    public static GapExpandDirection? ExpanderHit(GapBar gap, float xFromContentLeft)
    {
        var icons = ExpanderIconsFor(gap);
        if (icons.Length == 0) return null;
        if (icons.Length == 1) return icons[0];

        var x = ExpanderPadLeft;
        foreach (var dir in icons)
        {
            if (xFromContentLeft >= x && xFromContentLeft <= x + ExpanderCellWidth) return dir;
            x += ExpanderCellWidth;
        }
        return null;
    }

    private void DrawBannerRow(ICanvas c, DiffRow.Banner b, in DiffRowPaint p)
    {
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(p.Left, p.Bottom, p.Width, LineHeight),
            Style = SolidBgStyle(Styles.SectionBackground),
            ZIndex = p.Z,
        });
        DrawMonoText(c, b.Text, p.Left + BannerPaddingX, p.Bottom,
            p.Width - BannerPaddingX * 2, Styles.SectionMutedText, TextAlignment.Start, p.Z + 1);
    }

    private void DrawHunkSeparatorRow(ICanvas c, DiffRow.HunkSeparator s, in DiffRowPaint p)
    {
        // The whole bar is the expander's click target, so it tints as one on hover. Only gap
        // bars are hoverable, so a plain (null-gap) separator never arrives hovered.
        var barBg = p.ExpanderHovered ? Styles.ExpanderHoverBackground : Styles.SectionBackground;
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(p.Left, p.Bottom, p.Width, LineHeight),
            Style = SolidBgStyle(barBg),
            ZIndex = p.Z,
        });

        var textX = p.Left + BannerPaddingX;
        if (s.Gap is { } gap)
        {
            // Expander icon at the far left, over the gutter columns; the bar text shifts
            // right of it.
            textX = Math.Max(textX, DrawExpanderIcons(c, gap, p.Left, p.Bottom, p.Z) + BannerPaddingX);
        }

        var cursorX = textX;
        if (s.Range.Length > 0)
        {
            var rangeWidth = s.Range.Length * MonoAdvance;
            DrawMonoText(c, s.Range, cursorX, p.Bottom, rangeWidth,
                Styles.HunkSeparatorRangeText, TextAlignment.Start, p.Z + 1);
            cursorX += rangeWidth + HunkHeaderGap;
        }

        if (s.Header != null)
        {
            var headerWidth = Math.Max(0f, p.Left + p.Width - BannerPaddingX - cursorX);
            if (headerWidth > 0)
            {
                var header = FitHeader(s.Header, headerWidth);
                DrawMonoText(c, header, cursorX, p.Bottom, headerWidth,
                    Styles.SectionMutedText, TextAlignment.Start, p.Z + 1);
                cursorX += Math.Min(DiffText.VisualCells(header) * MonoAdvance, headerWidth) + HunkHeaderGap;
            }
        }

        // The muted "… N hidden lines" label; omitted on the EOF bar until the count is exact.
        if (s.Gap is { HiddenCount: int hidden })
        {
            var label = _loc.Strings.Value.DiffHiddenLines(hidden);
            var labelWidth = Math.Max(0f, p.Left + p.Width - BannerPaddingX - cursorX);
            if (labelWidth > 0)
                DrawMonoText(c, label, cursorX, p.Bottom, labelWidth,
                    Styles.SectionMutedText, TextAlignment.Start, p.Z + 1);
        }
    }

    // A containment path reads from the outside in, so the member it ends on is the part worth
    // keeping: what does not fit is dropped off the front, behind an ellipsis. Start-aligned text
    // would otherwise clip away exactly the name the header exists to show.
    internal string FitHeader(string header, float width)
    {
        var cells = (int)(width / MonoAdvance);
        if (DiffText.VisualCells(header) <= cells) return header;
        return HeaderEllipsis + DiffText.SuffixWithin(header, cells - DiffText.VisualCells(HeaderEllipsis));
    }

    // Draws a GapBar's accent expander glyphs and returns the x just past the last cell. Shared by
    // the separator bars and the tear row; hover is a whole-bar tint painted by the row, not here.
    private float DrawExpanderIcons(ICanvas c, GapBar gap, float left, float bottom, int z)
    {
        var x = left + ExpanderPadLeft;
        ExpanderIconStyle.TextColor = Styles.ExpanderIcon;
        foreach (var dir in ExpanderIconsFor(gap))
        {
            c.DrawText(new DrawTextInputs
            {
                Position = new RectF(x, bottom, ExpanderCellWidth - ExpanderChipGap, LineHeight),
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
    private void DrawTearRow(ICanvas c, DiffRow.Tear t, in DiffRowPaint p)
    {
        // The tear is a click target too; on hover it tints as a whole bar like the separators
        // (idle it stays on the plain background so the torn strip reads as empty).
        if (p.ExpanderHovered)
        {
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(p.Left, p.Bottom, p.Width, LineHeight),
                Style = SolidBgStyle(Styles.ExpanderHoverBackground),
                ZIndex = p.Z,
            });
        }
        var cursorX = DrawExpanderIcons(c, t.Gap, p.Left, p.Bottom, p.Z) + BannerPaddingX;
        if (t.Gap.HiddenCount is int hidden)
        {
            var label = _loc.Strings.Value.DiffHiddenLines(hidden);
            var labelWidth = DiffText.VisualCells(label) * MonoAdvance;
            DrawMonoText(c, label, cursorX, p.Bottom, labelWidth,
                Styles.SectionMutedText, TextAlignment.Start, p.Z + 1);
            cursorX += labelWidth + HunkHeaderGap;
        }
        DrawTearLine(c, cursorX, p.Left + p.Width, p.Bottom + LineHeight / 2f, p.Viewport, p.Z + 1);
    }

    // A thin zigzag polyline centered on the row. Segment endpoints derive from the tear's
    // content-space start, so the pattern holds its phase under horizontal scroll; only the
    // segments intersecting the viewport are issued (the pattern can span the whole content
    // width, most of it scrolled out of view).
    private void DrawTearLine(ICanvas c, float from, float to, float centerY, RectF viewport, int z)
    {
        if (to - from < TearHalfPeriod * 2) return;
        var visFrom = Math.Max(from, viewport.Left - TearHalfPeriod);
        var visTo = Math.Min(to, viewport.Right + TearHalfPeriod);
        if (visTo <= visFrom) return;

        var kFrom = Math.Max(0, (int)((visFrom - from) / TearHalfPeriod));
        var kTo = Math.Min((int)((to - from) / TearHalfPeriod), (int)((visTo - from) / TearHalfPeriod) + 1);
        var color = Styles.LineNumberText;
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

    public static GapExpandDirection[] ExpanderIconsFor(GapBar gap)
    {
        if (gap.ShowUnfold) return UnfoldOnly;
        if (gap.ShowDown && gap.ShowUp) return DownAndUp;
        if (gap.ShowDown) return DownOnly;
        if (gap.ShowUp) return UpOnly;
        return NoExpanders;
    }

    private void DrawLineRow(ICanvas c, DiffRow.Line l, in DiffRowPaint p)
    {
        DrawRowBackground(c, l, p);
        var textLeft = DrawGutterAndGlyph(c, l, p);
        if (l.Emphasis is { Count: > 0 } ranges)
            DrawIntraLineEmphasis(c, l, ranges, textLeft, p.Bottom, p.Z);
        // Above the emphasis wash (same layer, drawn after) and below the text, which stays
        // fully legible through the selection tint.
        if (p.Selection is { } selection)
            DrawSelection(c, l.Text.Expanded, selection, textLeft, p.Bottom, p.Z + 1);
        DrawLineText(c, l, textLeft, p.Bottom, p.Left + p.Width, p.Z + 2);

        // After the text and outside it: the chip is chrome standing in for the body, not part of
        // the row's characters, so nothing selects it and nothing measures a caret against it.
        if (l.Fold is { Chip: true })
            DrawFoldChip(c, l, textLeft, p);
    }

    private void DrawFoldChip(ICanvas c, DiffRow.Line l, float textLeft, in DiffRowPaint p)
    {
        var (x, width) = FoldChipBounds(l, textLeft);
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(x, p.Bottom + FoldChipInsetY, width, LineHeight - FoldChipInsetY * 2),
            Style = new RectStyle
            {
                BackgroundColor = Styles.FoldChipBackground,
                BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            },
            ZIndex = p.Z + 2,
        });
        DrawMonoText(
            c, DiffRowSet.FoldChipText, x, p.Bottom, width,
            Styles.LineNumberText, TextAlignment.Start, p.Z + 3);
    }

    /// <summary>Where a collapsed fold's pill sits on its row, for drawing it and for clicking it.</summary>
    public (float X, float Width) FoldChipBounds(DiffRow.Line l, float textLeft) => (
        textLeft + DiffText.VisualCells(l.Text.Expanded) * MonoAdvance,
        DiffText.VisualCells(DiffRowSet.FoldChipText) * MonoAdvance);

    private const float FoldChipInsetY = 1f;

    // The selected slice as one rect on the monospace cell grid — the same grid the hit-test
    // inverts, so the highlight's edges land exactly where a click would put the caret.
    private void DrawSelection(
        ICanvas c, string text, in DiffRowSelection selection, float textLeft, float bottom, int z)
    {
        var startCell = DiffText.CellsBefore(text, selection.StartChar.Value);
        var endCell = DiffText.CellsBefore(text, selection.EndChar.Value);
        var width = (endCell - startCell) * MonoAdvance;
        if (selection.IncludesEol) width += MonoAdvance * SelectionEolWidth;
        if (width <= 0f) return;

        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(textLeft + startCell * MonoAdvance, bottom, width, LineHeight),
            Style = SolidBgStyle(Styles.SelectionBackground),
            ZIndex = z,
        });
    }

    private void DrawRowBackground(ICanvas c, DiffRow.Line l, in DiffRowPaint p)
    {
        var bg = l.Kind switch
        {
            DiffLineKind.Added => Styles.LineAddedBackground,
            DiffLineKind.Removed => Styles.LineRemovedBackground,
            _ => Styles.Background,
        };
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(p.Left, p.Bottom, p.Width, LineHeight),
            Style = SolidBgStyle(bg),
            ZIndex = p.Z,
        });
    }

    // Draws the line-number gutter(s) and the +/-/space kind glyph, returning the x where the
    // line text begins.
    private float DrawGutterAndGlyph(ICanvas c, DiffRow.Line l, in DiffRowPaint p)
    {
        var (glyph, glyphColor) = l.Kind switch
        {
            DiffLineKind.Added => ("+", Styles.LineAddedGlyph),
            DiffLineKind.Removed => ("-", Styles.LineRemovedGlyph),
            _ => (" ", Styles.LineContextGlyph),
        };

        var x = p.Left;
        // Full-file mode shows only the new-side gutter; diff mode shows old|new.
        if (!p.SingleGutter)
        {
            DrawMonoText(c, l.OldNumber, x, p.Bottom, p.GutterWidth,
                Styles.LineNumberText, TextAlignment.End, p.Z + 2);
            x += p.GutterWidth + ColumnGap;
        }
        DrawMonoText(c, l.NewNumber, x, p.Bottom, p.GutterWidth,
            Styles.LineNumberText, TextAlignment.End, p.Z + 2);
        x += p.GutterWidth + ColumnGap;

        if (p.FoldColumn)
        {
            if (l.Fold is { Chevron: true } fold)
                DrawFoldChevron(c, fold, x, p);
            x += FoldColumnWidth;
        }

        // Full-file mode reads as an editor, and an editor rules a line down its left margin. Diff
        // mode does not: two gutters and a +/- glyph already say where the numbering ends.
        if (p.SingleGutter)
        {
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(x, p.Bottom, GutterRuleWidth, LineHeight),
                Style = SolidBgStyle(Styles.GutterRule),
                ZIndex = p.Z + 1,
            });
        }

        DrawMonoText(c, glyph, x, p.Bottom, GlyphColumnWidth, glyphColor, TextAlignment.Center, p.Z + 2);
        return LineTextOriginX(p.Left, p.GutterWidth, p.SingleGutter, p.FoldColumn);
    }

    // Dim until pointed at, like the gap expanders: a chevron beside every declaration with a body
    // is a lot of chrome to keep at full contrast on a file the reader is only reading.
    private void DrawFoldChevron(ICanvas c, in FoldMark fold, float x, in DiffRowPaint p)
    {
        var style = new TextStyle
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = FoldChevronSize,
            TextColor = p.FoldHovered ? Styles.LineText : Styles.LineNumberText,
            HorizontalAlignment = TextAlignment.Center,
            VerticalAlignment = TextAlignment.Center,
        };
        c.DrawText(new DrawTextInputs
        {
            Position = new RectF(x, p.Bottom, FoldColumnWidth, LineHeight),
            Text = fold.Collapsed ? LucideIcons.ChevronRight : LucideIcons.ChevronDown,
            Style = style,
            ZIndex = p.Z + 2,
        });
    }

    private const float FoldChevronSize = 11f;
    public const float GutterRuleWidth = 1f;

    // Intra-line emphasis: a stronger background tint over the changed characters, layered between
    // the line bg (z) and the text (z + 2). Walk the ranges incrementally, carrying cx forward
    // exactly as DrawLineText does, never re-measuring from column 0.
    private void DrawIntraLineEmphasis(
        ICanvas c, DiffRow.Line l, IReadOnlyList<CharRange> ranges, float textLeft, float bottom, int z)
    {
        var emBg = l.Kind == DiffLineKind.Removed
            ? Styles.LineRemovedEmphasisBackground
            : Styles.LineAddedEmphasisBackground;
        var text = l.Text.Expanded;
        var len = text.Length;
        var col = 0;
        var cx = textLeft;
        foreach (var rng in ranges)
        {
            // ForPair promises sorted, non-overlapping, in-bounds ranges; this clamp is the
            // backstop so a regression there shows up as a wrong-looking rect, never a per-frame
            // Substring crash in the render loop.
            var start = Math.Clamp(rng.Start, col, len);
            var end = Math.Clamp(rng.Start + rng.Length, start, len);
            if (start > col)
                cx += c.MeasureTextWidth(text.Substring(col, start - col), MonoStartStyle);
            var w = c.MeasureTextWidth(text.Substring(start, end - start), MonoStartStyle);
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(cx, bottom, w, LineHeight),
                Style = SolidBgStyle(emBg),
                ZIndex = z + 1,
            });
            cx += w;
            col = end;
        }
    }

    // Draws the line's text either as one run (no spans → plain, identical to before) or as a
    // sequence of colored runs interleaved with base-colored gaps. The font is monospace, so
    // each run sits at textStart + column*advance and every DrawText batches into one GPU draw.
    private void DrawLineText(ICanvas c, DiffRow.Line l, float textStart, float bottom, float maxRight, int z)
    {
        var text = l.Text.Expanded;
        var spans = l.Spans;
        if (spans == null || spans.Count == 0)
        {
            DrawMonoText(c, text, textStart, bottom, Math.Max(0f, maxRight - textStart),
                Styles.LineText, TextAlignment.Start, z);
            return;
        }

        var len = text.Length;
        var col = 0;
        var x = textStart;
        foreach (var span in spans)
        {
            var start = Math.Clamp(span.Start, 0, len);
            var end = Math.Clamp(span.Start + span.Length, 0, len);
            if (start > col)
                x = DrawTextRun(c, text, col, start, x, bottom, maxRight, Styles.LineText, z);
            if (end > start)
                x = DrawTextRun(c, text, start, end, x, bottom, maxRight, SlotColor(span.Slot), z);
            if (end > col) col = end;
        }
        if (col < len)
            DrawTextRun(c, text, col, len, x, bottom, maxRight, Styles.LineText, z);
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
        TokenColorSlot.Keyword => Styles.Syntax.Keyword,
        TokenColorSlot.String => Styles.Syntax.String,
        TokenColorSlot.Comment => Styles.Syntax.Comment,
        TokenColorSlot.Number => Styles.Syntax.Number,
        TokenColorSlot.Type => Styles.Syntax.Type,
        TokenColorSlot.Function => Styles.Syntax.Function,
        TokenColorSlot.Variable => Styles.Syntax.Variable,
        TokenColorSlot.Operator => Styles.Syntax.Operator,
        TokenColorSlot.Punctuation => Styles.Syntax.Punctuation,
        TokenColorSlot.Constant => Styles.Syntax.Constant,
        TokenColorSlot.Heading => Styles.Syntax.Heading,
        TokenColorSlot.Emphasis => Styles.Syntax.Emphasis,
        TokenColorSlot.Link => Styles.Syntax.Link,
        TokenColorSlot.Code => Styles.Syntax.Code,
        TokenColorSlot.Quote => Styles.Syntax.Quote,
        _ => Styles.LineText,
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
            Position = new RectF(left, bottom, width, LineHeight),
            Text = text,
            Style = style,
            ZIndex = z,
        });
    }

    private static RectStyle SolidBgStyle(uint color) => new() { BackgroundColor = color };
}
