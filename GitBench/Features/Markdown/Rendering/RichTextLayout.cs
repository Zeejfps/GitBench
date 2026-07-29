using ZGF.Gui;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// One positioned slice of a run on a laid-out line. <paramref name="Start"/>/<paramref name="Length"/>
/// index the run's <see cref="RichTextRun.Text"/> in UTF-16 units; <paramref name="X"/> is the
/// segment's left edge relative to the line's origin (the first segment of a line sits at 0);
/// <paramref name="Width"/> is the measured width of the slice in the run's style. Slices tile
/// the run text the way <see cref="TextWrapper.WrapRanges"/> ranges tile their input: a
/// soft-wrapped line ends where the next begins — spaces at the break stay on the segment they
/// follow — while a '\n' appears in no segment.
/// </summary>
internal readonly record struct RichTextSegment(int RunIndex, int Start, int Length, float X, float Width);

/// <summary>
/// One visual line: its segments left-to-right, the line's total advance
/// (<paramref name="Width"/>), and its <paramref name="Height"/> — the tallest line height among
/// the styles of the runs present on the line (a forced-empty line keeps the height of the run
/// whose '\n' produced it). Lines with no segments occur only from consecutive or trailing
/// newlines.
/// </summary>
internal sealed record RichTextLine(IReadOnlyList<RichTextSegment> Segments, float Width, float Height);

/// <summary>
/// A finished layout: lines top-to-bottom, <paramref name="Height"/> the sum of line heights,
/// <paramref name="MaxLineWidth"/> the widest line — the natural (unwrapped-or-as-wrapped) width
/// the view reports as its intrinsic measure.
/// </summary>
internal sealed record RichTextLayoutResult(IReadOnlyList<RichTextLine> Lines, float Height, float MaxLineWidth);

/// <summary>
/// The run-aware wrap engine behind <see cref="RichTextView"/> and (later) table cells: greedy
/// UAX-14-lite wrap over styled runs, measuring each slice with its own run's
/// <see cref="TextStyle"/> through the canvas.
/// <para>
/// Break behavior is <see cref="TextWrapper"/>'s, and for single-style input the line splits must
/// match <see cref="TextWrapper.WrapRanges"/> exactly (the shared test corpus is the gate):
/// breaks at spaces and after separator punctuation (<c>/ \ - _ . :</c>), CJK breaks between
/// code points, kinsoku prohibitions, and code-point splitting for a chunk with no break
/// opportunity that is wider than the line. A run boundary is <b>not</b> a break opportunity by
/// itself — adjacent runs break at their seam only where the concatenated text would break
/// anyway (a space before the seam, a separator ending the left run, CJK on either side).
/// A non-positive <paramref name="maxWidth"/> disables wrapping ('\n' still breaks). An empty
/// run list (or runs with no text at all) produces zero lines and zero height.
/// </para>
/// </summary>
internal static class RichTextLayout
{
    /// <summary>Lays <paramref name="runs"/> out into lines of positioned segments no wider than
    /// <paramref name="maxWidth"/>, measuring through <paramref name="canvas"/>.</summary>
    public static RichTextLayoutResult Layout(ICanvas canvas, IReadOnlyList<RichTextRun> runs, float maxWidth)
    {
        throw new NotImplementedException();
    }
}
