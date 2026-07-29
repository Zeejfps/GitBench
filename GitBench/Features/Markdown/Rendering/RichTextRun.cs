using ZGF.Gui;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// One pre-styled visual run — the input unit of <see cref="RichTextLayout"/> and
/// <see cref="RichTextView"/>. Deliberately markdown-agnostic: Step 5's run builder maps the
/// AST's <c>InlineRun</c>s onto these, but anything that can produce styled runs renders through
/// the same primitive.
/// <para>
/// <paramref name="Style"/> is the complete measure/draw style (family, size, weight, color, …):
/// the layout measures every slice of the run with it, and the view passes it to
/// <see cref="ICanvas.DrawText"/> for the run's segments. Each run must carry a style instance
/// whose values are stable for the frame — the draw path hands the style to the canvas per
/// segment, so a single shared instance mutated between runs (the <c>DiffRowPainter</c> trick)
/// would alias; give each distinct look its own instance.
/// </para>
/// <para>
/// The flags are the view's decoration contract, orthogonal to <paramref name="Style"/>:
/// <paramref name="IsCode"/> draws the inline-code chip background behind the run's segments,
/// <paramref name="Underline"/> draws a rule under them in the run's text color, and a non-null
/// <paramref name="LinkUrl"/> makes them link targets for hit-testing
/// (<see cref="RichTextView.LinkAt"/>) and hover recoloring.
/// </para>
/// <para>
/// A '\n' anywhere in <paramref name="Text"/> forces a line break at that position — this is how
/// markdown hard breaks arrive (the AST emits them as dedicated <c>"\n"</c> runs). No other
/// control characters are interpreted; '\r' is not special.
/// </para>
/// </summary>
internal sealed record RichTextRun(
    string Text,
    TextStyle Style,
    bool IsCode = false,
    bool Underline = false,
    string? LinkUrl = null);
