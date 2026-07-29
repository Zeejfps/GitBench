using ZGF.Geometry;
using ZGF.Gui;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// The per-segment draw sequence and slice-string building shared by <see cref="RichTextView"/>
/// and <see cref="MarkdownTableView"/>, so paragraph and table-cell segments can never drift
/// apart visually. Both views keep their own caches and pass their own resolved
/// <see cref="TextStyle"/> per segment (the paragraph view hover-recolors, the table always uses
/// the run's style) and their own chip <see cref="RectStyle"/> instance (the canvas retains style
/// references, so the instance must be per view).
/// </summary>
internal static class RichTextDrawing
{
    /// <summary>Thickness of the underline rule drawn under <see cref="RichTextRun.Underline"/>
    /// segments.</summary>
    public const float UnderlineThickness = 1f;

    /// <summary>Thickness of the strike rule drawn through <see cref="RichTextRun.Strikethrough"/>
    /// segments.</summary>
    public const float StrikeThickness = 1f;

    /// <summary>
    /// Height of the strike rule above a segment's line band, as a fraction of the band height.
    /// <see cref="ICanvas"/> exposes line height only — no ascent, descent or x-height — so this
    /// approximates the x-height midpoint from the proportions common to UI text faces: a
    /// descent of about 0.2 of the line height puts the baseline there, and an x-height of about
    /// 0.42 of it puts its midpoint about 0.2 higher again.
    /// </summary>
    public const float StrikeHeightFraction = 0.4f;

    /// <summary>Corner radius of the inline-code chip drawn behind <see cref="RichTextRun.IsCode"/>
    /// segments.</summary>
    public const float ChipCornerRadius = 3f;

    /// <summary>
    /// Draws one laid-out segment, bottom layer first: the inline-code chip (when the run is code
    /// and <paramref name="chipBackground"/> is nonzero) at <paramref name="z"/>, strictly below
    /// the decoration rules and the text at <paramref name="z"/> + 1. The rules are independent —
    /// a struck link draws both — and draw in the drawn text color, i.e. <paramref name="style"/>'s,
    /// so a hover recolor carries them along.
    /// </summary>
    public static void DrawSegment(
        ICanvas c,
        RectF rect,
        RichTextRun run,
        TextStyle style,
        string text,
        uint chipBackground,
        RectStyle chipStyle,
        int z)
    {
        if (run.IsCode && chipBackground != 0)
        {
            chipStyle.BackgroundColor = chipBackground;
            c.DrawRect(new DrawRectInputs
            {
                Position = rect,
                Style = chipStyle,
                ZIndex = z, // strictly below the segment's text
            });
        }

        if (run.Underline)
        {
            var y = rect.Bottom + UnderlineThickness;
            c.DrawLine(new DrawLineInputs
            {
                Start = new PointF(rect.Left, y),
                End = new PointF(rect.Right, y),
                Thickness = UnderlineThickness,
                Color = style.TextColor.Value,
                ZIndex = z + 1,
            });
        }

        if (run.Strikethrough)
        {
            var y = rect.Bottom + rect.Height * StrikeHeightFraction;
            c.DrawLine(new DrawLineInputs
            {
                Start = new PointF(rect.Left, y),
                End = new PointF(rect.Right, y),
                Thickness = StrikeThickness,
                Color = style.TextColor.Value,
                ZIndex = z + 1,
            });
        }

        c.DrawText(new DrawTextInputs
        {
            Position = rect,
            Text = text,
            Style = style,
            ZIndex = z + 1,
        });
    }

    /// <summary>Appends the slice string of every segment of <paramref name="layout"/>, in line
    /// order, to <paramref name="output"/> — reusing the run's whole text when the segment covers
    /// it, so steady-state draws can index these instead of substringing.</summary>
    public static void BuildSegmentTexts(
        IReadOnlyList<RichTextRun> runs, RichTextLayoutResult layout, List<string> output)
    {
        foreach (var line in layout.Lines)
        {
            foreach (var seg in line.Segments)
            {
                var text = runs[seg.RunIndex].Text;
                output.Add(seg.Start == 0 && seg.Length == text.Length
                    ? text
                    : text.Substring(seg.Start, seg.Length));
            }
        }
    }
}
