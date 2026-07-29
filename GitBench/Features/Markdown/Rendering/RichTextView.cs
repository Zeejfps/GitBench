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
        throw new NotImplementedException();
    }

    /// <summary>The <see cref="RichTextRun.LinkUrl"/> of the link segment under
    /// <paramref name="point"/> (view coordinate space, as mouse events arrive), or null when the
    /// point is over no link segment.</summary>
    public string? LinkAt(PointF point)
    {
        throw new NotImplementedException();
    }

    protected override float MeasureWidthIntrinsic()
    {
        throw new NotImplementedException();
    }

    protected override float MeasureHeightIntrinsic(float availableWidth)
    {
        throw new NotImplementedException();
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        throw new NotImplementedException();
    }
}
