using ZGF.Geometry;
using ZGF.Gui;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Draws a list of <see cref="RichTextRun"/>s as wrapped, positioned segments — the markdown
/// renderer's paragraph body. Mirrors <see cref="ZGF.Gui.Views.TextView"/>'s shape: measures and
/// draws through the <see cref="ICanvas"/> it was built with, and recomputes its
/// <see cref="RichTextLayout"/> only when the width or the runs change (the
/// <c>_wrappedForWidth</c> pattern), because measure and draw run every frame.
/// <para>
/// Drawing, bottom layer first: inline-code chip backgrounds (a themed rect behind every
/// <see cref="RichTextRun.IsCode"/> segment), then the selection band over them, then decoration
/// rules (<see cref="ICanvas.DrawLine"/> in the segment's text color under
/// <see cref="RichTextRun.Underline"/> segments and through <see cref="RichTextRun.Strikethrough"/>
/// ones), then one <see cref="ICanvas.DrawText"/> per segment in the run's own style. A
/// hovered link (<see cref="SetHoveredLink"/>) draws its segments in <see cref="LinkHoverColor"/>
/// instead of the run's color. Lines stack from <c>Position.Top</c> downward.
/// </para>
/// <para>
/// Segment rects are kept for hit-testing: <see cref="LinkAt"/> maps a point in the view's
/// coordinate space (the same space mouse events arrive in) to the <see cref="RichTextRun.LinkUrl"/>
/// under it, and <see cref="CharIndexAt"/> maps one to an offset into <see cref="SelectableText"/>.
/// <see cref="LinkController"/> drives hover and click through the former;
/// <see cref="MarkdownSelectionController"/> drags a selection through the latter, which this view
/// paints back as a band per line behind the selected characters.
/// </para>
/// </summary>
internal sealed class RichTextView : View
{
    private readonly ICanvas _canvas;
    private IReadOnlyList<RichTextRun> _runs = Array.Empty<RichTextRun>();

    // Wrapped layout cache, TextView's _wrappedForWidth pattern: valid while the width is
    // (nearly) unchanged and the runs are the same list instance.
    private RichTextLayoutResult? _layout;
    private float _layoutForWidth;
    private IReadOnlyList<RichTextRun>? _layoutForRuns;

    // Natural (unwrapped) layout for intrinsic width / unconstrained height, cached separately so
    // alternating measure-width / measure-height calls don't evict each other.
    private RichTextLayoutResult? _natural;
    private IReadOnlyList<RichTextRun>? _naturalForRuns;

    // Per-segment slice strings for DrawText, rebuilt only when the layout instance changes so
    // steady-state draws allocate nothing.
    private readonly List<string> _segmentTexts = new();
    private RichTextLayoutResult? _segmentTextsFor;

    // Hover recolor styles, one copy per run so each DrawText call hands the canvas a style
    // instance whose values are stable — never a shared instance mutated between calls.
    private TextStyle?[]? _hoverStyles;
    private IReadOnlyList<RichTextRun>? _hoverStylesForRuns;
    private uint _hoverStylesColor;

    // The runs' text as one string plus each run's start offset in it — the offset space a
    // selection is expressed in. Rebuilt only when the run list instance changes.
    private string _text = string.Empty;
    private int[] _runStarts = [0];
    private IReadOnlyList<RichTextRun>? _textForRuns;

    // Per-line character ranges, cached alongside the layout they came from so selection painting
    // and hit-testing walk lines without re-deriving offsets every frame.
    private readonly List<LineSpan> _lineSpans = new();
    private RichTextLayoutResult? _lineSpansFor;

    private readonly RectStyle _chipStyle =
        new() { BorderRadius = BorderRadiusStyle.All(RichTextDrawing.ChipCornerRadius) };

    private readonly RectStyle _selectionStyle = new();

    public RichTextView(ICanvas canvas)
    {
        _canvas = canvas;
        Accessibility = new AccessibilityInfo(AccessibilityRole.Text);
    }

    /// <summary>The styled runs to lay out and draw. Assigning a different list instance
    /// invalidates the cached layout (runs are records; build a new list to change content).</summary>
    public IReadOnlyList<RichTextRun> Runs
    {
        get => _runs;
        set => SetField(ref _runs, value);
    }

    /// <summary>Background color of the inline-code chip drawn behind code segments. Comes from
    /// the theme via the <see cref="RichText"/> widget; 0 draws no chip.</summary>
    public uint CodeChipBackground { get; set; }

    /// <summary>Text color for the hovered link's segments. Comes from the theme.</summary>
    public uint LinkHoverColor { get; set; }

    /// <summary>The url whose segments currently draw hovered, or null. Set by
    /// <see cref="SetHoveredLink"/>.</summary>
    public string? HoveredLinkUrl { get; private set; }

    /// <summary>Marks the link with <paramref name="url"/> (all of its segments) as hovered, or
    /// clears hover when null; redraws on change. Driven by <see cref="LinkController"/>.</summary>
    public void SetHoveredLink(string? url)
    {
        if (HoveredLinkUrl == url)
            return;
        HoveredLinkUrl = url;
        SetDirty();
    }

    /// <summary>The <see cref="RichTextRun.LinkUrl"/> of the link segment under
    /// <paramref name="point"/> (view coordinate space, as mouse events arrive), or null when the
    /// point is over no link segment.</summary>
    public string? LinkAt(PointF point)
    {
        if (_runs.Count == 0)
            return null;

        var layout = LayoutFor(Position.Width);
        var left = Position.Left;
        var top = Position.Top;
        foreach (var line in layout.Lines)
        {
            var bottom = top - line.Height;
            if (point.Y <= top && point.Y > bottom)
            {
                foreach (var seg in line.Segments)
                {
                    var segLeft = left + seg.X;
                    if (point.X >= segLeft && point.X < segLeft + seg.Width)
                        return _runs[seg.RunIndex].LinkUrl;
                }
                return null;
            }
            top = bottom;
        }

        return null;
    }

    /// <summary>Highlight color painted behind selected characters. Comes from the theme via the
    /// <see cref="RichText"/> widget; 0 paints no highlight.</summary>
    public uint SelectionBackground { get; set; }

    /// <summary>The surface selection this leaf belongs to while it is registered, or null when
    /// the markdown around it offers none. Set by <see cref="IMarkdownSelectionScope.Register"/>.</summary>
    public IMarkdownSelectionScope? Selection { get; set; }

    /// <summary>The runs' text as one string — what a selection offsets into, and what a copy of
    /// the whole leaf yields.</summary>
    public string SelectableText
    {
        get
        {
            EnsureRunText();
            return _text;
        }
    }

    /// <summary>Redraws this leaf after the selection covering it changed.</summary>
    public void SelectionChanged() => SetDirty();

    /// <summary>The offset into <see cref="SelectableText"/> nearest <paramref name="point"/> (view
    /// coordinate space, as mouse events arrive). A point above the first line resolves to 0 and one
    /// below the last to the end, so a drag that runs off the block still selects through it.</summary>
    public int CharIndexAt(PointF point)
    {
        EnsureRunText();
        if (_text.Length == 0)
            return 0;

        var layout = LayoutFor(Position.Width);
        if (layout.Lines.Count == 0)
            return 0;
        EnsureLineSpans(layout);

        var depth = Position.Top - point.Y;
        if (depth < 0f)
            return 0;

        var index = 0;
        while (index < _lineSpans.Count - 1 && depth >= _lineSpans[index].Offset + _lineSpans[index].Height)
            index++;

        var span = _lineSpans[index];
        if (depth >= span.Offset + span.Height)
            return _text.Length;

        return CharIndexOnLine(layout.Lines[index], span, point.X - Position.Left);
    }

    protected override float MeasureWidthIntrinsic()
    {
        if (Width.IsSet)
            return Width;
        return NaturalLayout().MaxLineWidth;
    }

    protected override float MeasureHeightIntrinsic(float availableWidth)
    {
        // availableWidth <= 0 means "unconstrained": natural, '\n'-only line breaks.
        return availableWidth > 0f ? LayoutFor(availableWidth).Height : NaturalLayout().Height;
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        var layout = LayoutFor(Position.Width);
        if (layout.Lines.Count == 0)
            return;

        EnsureSegmentTexts(layout);

        var z = GetDrawZIndex();
        DrawSelection(c, layout, z);

        var left = Position.Left;
        var top = Position.Top;
        var segmentText = 0;
        foreach (var line in layout.Lines)
        {
            var bottom = top - line.Height;
            foreach (var seg in line.Segments)
            {
                var run = _runs[seg.RunIndex];
                var rect = new RectF(left + seg.X, bottom, seg.Width, line.Height);
                var hovered = HoveredLinkUrl != null && run.LinkUrl == HoveredLinkUrl;
                var style = hovered ? HoverStyleFor(seg.RunIndex) : run.Style;
                RichTextDrawing.DrawSegment(
                    c, rect, run, style, _segmentTexts[segmentText++],
                    CodeChipBackground, _chipStyle, z);
            }
            top = bottom;
        }
    }

    // One band per line the selection touches, on the layer between a segment's background and its
    // glyphs: over an opaque inline-code chip, under the text it highlights.
    private void DrawSelection(ICanvas c, RichTextLayoutResult layout, int z)
    {
        if (SelectionBackground == 0 || Selection is not { } selection)
            return;
        if (!selection.TrySpan(this, out var from, out var to))
            return;

        EnsureLineSpans(layout);
        _selectionStyle.BackgroundColor = SelectionBackground;
        for (var i = 0; i < _lineSpans.Count; i++)
        {
            var span = _lineSpans[i];
            var start = Math.Max(from, span.Start);
            var end = Math.Min(to, span.End);
            if (end <= start)
                continue;

            var line = layout.Lines[i];
            var left = OffsetX(line, start);
            var right = OffsetX(line, end);
            if (right <= left)
                continue;

            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(
                    Position.Left + left, Position.Top - span.Offset - span.Height, right - left, span.Height),
                Style = _selectionStyle,
                ZIndex = z + RichTextDrawing.SelectionLayer,
            });
        }
    }

    /// <summary>The x of a character offset within a laid-out line, relative to the line's origin.
    /// Offsets before the line's first segment sit at its left edge, offsets past its last at its
    /// right — which is what a selection running through the line asks for.</summary>
    private float OffsetX(RichTextLine line, int offset)
    {
        if (line.Segments.Count == 0)
            return 0f;

        foreach (var seg in line.Segments)
        {
            var segStart = GlobalStart(seg);
            if (offset <= segStart)
                return seg.X;
            if (offset < segStart + seg.Length)
            {
                var run = _runs[seg.RunIndex];
                return seg.X + _canvas.MeasureTextPrefix(
                    run.Text.AsSpan(seg.Start, seg.Length), offset - segStart, run.Style);
            }
        }

        var last = line.Segments[^1];
        return last.X + last.Width;
    }

    // The character boundary nearest x on a line: the segment under x, then a binary search for the
    // boundary inside it, rounding to whichever side of x is closer.
    private int CharIndexOnLine(RichTextLine line, LineSpan span, float x)
    {
        foreach (var seg in line.Segments)
        {
            if (x >= seg.X + seg.Width)
                continue;

            var segStart = GlobalStart(seg);
            if (x <= seg.X)
                return segStart;

            var run = _runs[seg.RunIndex];
            var text = run.Text.AsSpan(seg.Start, seg.Length);
            var target = x - seg.X;
            var lo = 0;
            var hi = seg.Length;
            while (lo < hi)
            {
                var mid = lo + (hi - lo + 1) / 2;
                if (_canvas.MeasureTextPrefix(text, mid, run.Style) <= target)
                    lo = mid;
                else
                    hi = mid - 1;
            }

            if (lo < seg.Length)
            {
                var before = _canvas.MeasureTextPrefix(text, lo, run.Style);
                var after = _canvas.MeasureTextPrefix(text, lo + 1, run.Style);
                if (after - target < target - before)
                    lo++;
            }
            return segStart + lo;
        }

        return span.End;
    }

    /// <summary>Where a segment begins in <see cref="SelectableText"/> — the offset space
    /// <see cref="RichTextLayout.ConcatenateRuns"/> defines and a selection is expressed in.</summary>
    private int GlobalStart(RichTextSegment segment) => _runStarts[segment.RunIndex] + segment.Start;

    private void EnsureRunText()
    {
        if (ReferenceEquals(_textForRuns, _runs))
            return;

        _text = RichTextLayout.ConcatenateRuns(_runs, out _runStarts);
        _textForRuns = _runs;
    }

    private void EnsureLineSpans(RichTextLayoutResult layout)
    {
        if (ReferenceEquals(_lineSpansFor, layout))
            return;

        EnsureRunText();
        _lineSpans.Clear();
        var offset = 0f;
        // A line with no segments is a bare '\n'; it collapses onto the offset just past the
        // previous line, which is where that newline lives in the concatenated text.
        var afterPrevious = 0;
        foreach (var line in layout.Lines)
        {
            int start, end;
            if (line.Segments.Count > 0)
            {
                var last = line.Segments[^1];
                start = GlobalStart(line.Segments[0]);
                end = GlobalStart(last) + last.Length;
            }
            else
            {
                start = end = afterPrevious;
            }

            _lineSpans.Add(new LineSpan(offset, line.Height, start, end));
            offset += line.Height;
            afterPrevious = end + 1;
        }
        _lineSpansFor = layout;
    }

    private RichTextLayoutResult LayoutFor(float maxWidth)
    {
        if (_layout == null
            || !ReferenceEquals(_layoutForRuns, _runs)
            || Math.Abs(maxWidth - _layoutForWidth) >= 0.5f)
        {
            _layout = RichTextLayout.Layout(_canvas, _runs, maxWidth);
            _layoutForWidth = maxWidth;
            _layoutForRuns = _runs;
        }
        return _layout;
    }

    private RichTextLayoutResult NaturalLayout()
    {
        if (_natural == null || !ReferenceEquals(_naturalForRuns, _runs))
        {
            _natural = RichTextLayout.Layout(_canvas, _runs, 0f);
            _naturalForRuns = _runs;
        }
        return _natural;
    }

    private void EnsureSegmentTexts(RichTextLayoutResult layout)
    {
        if (ReferenceEquals(_segmentTextsFor, layout))
            return;

        _segmentTexts.Clear();
        RichTextDrawing.BuildSegmentTexts(_runs, layout, _segmentTexts);
        _segmentTextsFor = layout;
    }

    private TextStyle HoverStyleFor(int runIndex)
    {
        if (_hoverStyles == null
            || !ReferenceEquals(_hoverStylesForRuns, _runs)
            || _hoverStylesColor != LinkHoverColor)
        {
            _hoverStyles = new TextStyle?[_runs.Count];
            _hoverStylesForRuns = _runs;
            _hoverStylesColor = LinkHoverColor;
        }
        return _hoverStyles[runIndex] ??= _runs[runIndex].Style with { TextColor = LinkHoverColor };
    }

    /// <summary>One laid-out line as the selection sees it: how far its top sits below the view's
    /// top, its height, and the character range it covers.</summary>
    private readonly record struct LineSpan(float Offset, float Height, int Start, int End);
}
