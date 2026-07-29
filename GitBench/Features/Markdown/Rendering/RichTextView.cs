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
/// <see cref="RichTextRun.IsCode"/> segment, below the text's z), underline rules
/// (<see cref="ICanvas.DrawLine"/> in the segment's text color for <see cref="RichTextRun.Underline"/>
/// segments), then one <see cref="ICanvas.DrawText"/> per segment in the run's own style. A
/// hovered link (<see cref="SetHoveredLink"/>) draws its segments in <see cref="LinkHoverColor"/>
/// instead of the run's color. Lines stack from <c>Position.Top</c> downward.
/// </para>
/// <para>
/// Segment rects are kept for hit-testing: <see cref="LinkAt"/> maps a point in the view's
/// coordinate space (the same space mouse events arrive in) to the <see cref="RichTextRun.LinkUrl"/>
/// under it. <see cref="LinkController"/> drives hover and click through these two members.
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

    private readonly RectStyle _chipStyle =
        new() { BorderRadius = BorderRadiusStyle.All(RichTextDrawing.ChipCornerRadius) };

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
}
